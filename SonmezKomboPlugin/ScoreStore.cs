using System.Text.Json;
using Serilog;

namespace SonmezKomboPlugin;

public class ScoreEntry
{
    public string Name { get; set; } = "";
    // Oyuncunun o zamana kadar kazandigi TOPLAM puan (dosya uyumlulugu icin alan adi "Best" kaldi)
    public long Best { get; set; }
}

// Toplam puanlar oyuncunun girdigi ISIMLE tutulur (buyuk/kucuk harf fark etmez).
// Puanlar toplanir: Add() ile eklenir, kazada sifirlanmaz.
public class ScoreStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, ScoreEntry> _data = new();   // diskteki deger + bu sunucunun henuz yazilmamis eklemeleri
    private readonly Dictionary<string, long> _pending = new(); // bu sunucunun diske henuz yazmadigi puan
    private readonly Dictionary<string, string> _pendingNames = new();

    public static string NormalizeKey(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return n.Length == 0 ? "?" : n;
    }

    public ScoreStore(string path)
    {
        _path = path;
        lock (_lock) LoadFromDisk();
    }

    // Diskteki dosyayi okur; bellek = disk + bu sunucunun yazilmamis eklemeleri.
    // Birden fazla sunucu ayni ScoreFile'i paylasirsa toplamlar ortaklasir (her sunucu sadece kendi eklemesini yazar).
    // Dosya yoksa true, okunabildiyse true, okunamadiysa false doner.
    private bool LoadFromDisk()
    {
        try
        {
            var fresh = new Dictionary<string, ScoreEntry>();
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var raw = JsonSerializer.Deserialize<Dictionary<string, ScoreEntry>>(json) ?? new();
                foreach (var e in raw.Values)
                {
                    if (e == null || e.Best <= 0) continue;
                    var k = NormalizeKey(e.Name);
                    if (fresh.TryGetValue(k, out var ex)) ex.Best += e.Best;
                    else fresh[k] = new ScoreEntry { Name = e.Name, Best = e.Best };
                }
            }
            foreach (var kv in _pending)
            {
                if (!fresh.TryGetValue(kv.Key, out var ex))
                {
                    ex = new ScoreEntry { Name = _pendingNames.GetValueOrDefault(kv.Key, kv.Key) };
                    fresh[kv.Key] = ex;
                }
                ex.Best += kv.Value;
            }
            foreach (var kv in _pendingNames)
                if (fresh.TryGetValue(kv.Key, out var ex2) && !string.IsNullOrWhiteSpace(kv.Value)) ex2.Name = kv.Value;
            _data = fresh;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SonmezKombo: skor dosyasi okunamadi");
            return false;
        }
    }

    // Oyuncu baglanirken / periyodik olarak diger sunucularin yazdigi toplamlari al
    public void Refresh()
    {
        lock (_lock) LoadFromDisk();
    }

    // Oyuncunun toplamina puan ekle
    public void Add(string key, string name, long delta)
    {
        if (delta <= 0) return;
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var e))
            {
                e = new ScoreEntry { Name = name };
                _data[key] = e;
            }
            e.Best += delta;
            if (!string.IsNullOrWhiteSpace(name))
            {
                e.Name = name;
                _pendingNames[key] = name;
            }
            _pending[key] = _pending.GetValueOrDefault(key) + delta;
        }
    }

    public long GetBest(string key)
    {
        lock (_lock) return _data.TryGetValue(key, out var e) ? e.Best : 0;
    }

    public int Rank(string key)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var e) || e.Best <= 0) return 0;
            return _data.Values.Count(x => x.Best > e.Best) + 1;
        }
    }

    public List<ScoreEntry> Top(int n)
    {
        lock (_lock)
            return _data.Values.Where(x => x.Best > 0)
                .OrderByDescending(x => x.Best)
                .Take(n)
                .Select(x => new ScoreEntry { Name = x.Name, Best = x.Best })
                .ToList();
    }

    public void Save()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            // Baska bir sunucu puan yazmis olabilir: once diski oku (okunamazsa uzerine yazma)
            if (!LoadFromDisk()) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _path, true);
                _pending.Clear();
                _pendingNames.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SonmezKombo: skor dosyasi yazilamadi");
            }
        }
    }
}
