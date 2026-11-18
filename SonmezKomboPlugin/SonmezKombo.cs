using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Plugin;
using AssettoServer.Utils;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace SonmezKomboPlugin;

public class SonmezKombo : BackgroundService
{
    private class PlayerState
    {
        public double Score;
        public long Best;
        public int Combo;
        public double ComboLeft;
        public int Lives;
        public double Time;
        public double Clock; // sifirlanmaz; LastPass bekleme suresi icin
        public double HitCooldown;
        public int MultiCount;
        public float Prox = 1f;
        public double ProxLeft;
        public double MultiLeft;
        public string Key = "";
        public Dictionary<int, (float fwd, float lat)> Prev = new();
        public Dictionary<int, double> LastPass = new();
    }

    private readonly SonmezKomboConfiguration _config;
    private readonly EntryCarManager _entryCarManager;
    private readonly ScoreStore _store;
    private readonly HubState _hub;
    private readonly ConcurrentDictionary<int, PlayerState> _states = new();

    public SonmezKombo(SonmezKomboConfiguration config,
        EntryCarManager entryCarManager,
        ScoreStore store,
        HubState hub,
        CSPServerScriptProvider scriptProvider,
        IHostApplicationLifetime applicationLifetime)
    {
        _config = config;
        _entryCarManager = entryCarManager;
        _store = store;
        _hub = hub;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("sonmezkombo.lua")
                           ?? throw new InvalidOperationException("sonmezkombo.lua bulunamadi");
        using var reader = new StreamReader(stream);
        scriptProvider.AddScript(reader.ReadToEnd().Replace("__HTTP_PORT__", _config.HttpPort.ToString(CultureInfo.InvariantCulture)).Replace("__SERVER_IP__", _config.ServerIp ?? ""), "sonmezkombo.lua");

        _entryCarManager.ClientConnected += OnClientConnected;
        _entryCarManager.ClientDisconnected += OnClientDisconnected;

        Log.Information("SonmezKombo: baslatildi. Calisma ortami: {Fw}. Lua script istemciye kaydedildi. Debug={Debug}, HubKey {Hub}",
            RuntimeInformation.FrameworkDescription, _config.Debug, _hub.Enabled ? "acik" : "bos (hub kapali)");
        Log.Information("SonmezKombo: NOT: oyunda HUD gorunmesi icin extra_cfg.yml'de EnableClientMessages: true olmali");
    }

    private PlayerState GetState(ACTcpClient client)
    {
        return _states.GetOrAdd(client.SessionId, _ =>
        {
            // Toplam puan, oyuncunun launcher'da yazdigi isme baglidir
            var key = ScoreStore.NormalizeKey(client.Name);
            return new PlayerState
            {
                Lives = _config.Lives,
                Key = key,
                Best = _store.GetBest(key)
            };
        });
    }

    private void OnClientConnected(ACTcpClient client, EventArgs args)
    {
        _states.TryRemove(client.SessionId, out _);
        _store.Refresh(); // baska sunucularda yapilan rekoru da al
        var st = GetState(client);
        Log.Information("SonmezKombo: oyuncu baglandi: {Name} (slot {Slot}), toplam puan={Best}", client.Name, client.SessionId, st.Best);
        client.Collision += OnCollision;
    }

    private void OnClientDisconnected(ACTcpClient client, EventArgs args)
    {
        _states.TryRemove(client.SessionId, out _);
        _store.Save();
    }

    private void OnCollision(ACTcpClient client, CollisionEventArgs args)
    {
        if (args.Speed < _config.MinCollisionSpeed) return;
        var st = GetState(client);
        lock (st)
        {
            if (st.HitCooldown > 0) return;
            st.HitCooldown = 1.2;
            st.Prox = 1f;
            st.ProxLeft = 0;
            st.Combo = 0;
            st.ComboLeft = 0;
            st.MultiCount = 0;
            st.MultiLeft = 0;
            st.Lives--;
            if (st.Lives <= 0)
            {
                st.Lives = _config.Lives;
                if (_config.ResetOnDeath)
                {
                    st.Score = 0;
                    st.Time = 0;
                }
            }
        }
        client.SendPacket(new KomboPopup { Points = 0, Kind = 1 });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const double dt = 0.05;
        int tick = 0;
        Log.Information("SonmezKombo: puan dongusu basladi (50 ms)");
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Update(dt);
                tick++;
                if (tick % 6 == 0) SendStates();
                if (tick % 20 == 0) SendLive();
                if (tick % 600 == 0) _store.Save();
                if (_config.Debug && tick % 100 == 0) LogHeartbeat();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SonmezKombo guncelleme hatasi");
            }
        }
        _store.Save();
    }

    // Debug acikken 5 sn'de bir: dongu calisiyor mu, kac oyuncu / AI arac gorunuyor
    private void LogHeartbeat()
    {
        int players = 0, ai = 0;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.AiControlled) ai++;
            var client = car.Client;
            if (client == null || !client.HasSentFirstUpdate) continue;
            players++;
            var st = GetState(client);
            float speed = car.Status.Velocity.Length() * 3.6f;
            lock (st)
            {
                Log.Information("SonmezKombo DEBUG nabiz: {Name} hiz={Speed:F0} puan={Score:F0} kombo={Combo} can={Lives}",
                    client.Name, speed, st.Score, st.Combo, st.Lives);
            }
        }
        Log.Information("SonmezKombo DEBUG nabiz: oyuncu={Players} ai_arac={Ai}", players, ai);
    }

    // Hub icin: su an sunucudaki oyuncular ve anlik puanlari
    public List<object> GetLive()
    {
        var list = new List<object>();
        foreach (var car in _entryCarManager.EntryCars)
        {
            var client = car.Client;
            if (client == null || !client.HasSentFirstUpdate) continue;
            var st = GetState(client);
            lock (st)
            {
                list.Add(new
                {
                    name = client.Name ?? "?",
                    score = (long)Math.Min(st.Score, MaxScore),
                    best = st.Best,
                    combo = st.Combo,
                    lives = st.Lives,
                    time = (int)st.Time
                });
            }
        }
        return list;
    }

    private static bool IsActive(EntryCar car) => car.AiControlled || (car.Client?.HasSentFirstUpdate ?? false);

    private void Update(double dt)
    {
        var cars = _entryCarManager.EntryCars;
        foreach (var car in cars)
        {
            var client = car.Client;
            if (client == null || !client.HasSentFirstUpdate) continue;
            var st = GetState(client);

            Vector3 v = car.Status.Velocity;
            float speed = v.Length() * 3.6f;
            var vh = new Vector2(v.X, v.Z);
            bool moving = vh.Length() > 1f;
            var fwd2 = moving ? Vector2.Normalize(vh) : Vector2.Zero;

            lock (st)
            {
                st.Time += dt;
                st.Clock += dt;
                if (st.HitCooldown > 0) st.HitCooldown -= dt;
                if (st.ProxLeft > 0)
                {
                    st.ProxLeft -= dt;
                    if (st.ProxLeft <= 0) st.Prox = 1f;
                }
                if (st.MultiLeft > 0)
                {
                    st.MultiLeft -= dt;
                    if (st.MultiLeft <= 0) st.MultiCount = 0;
                }
                if (st.ComboLeft > 0)
                {
                    st.ComboLeft -= dt;
                    if (st.ComboLeft <= 0) st.Combo = 0;
                }

                if (!ServerMode) continue;

                foreach (var other in cars)
                {
                    if (other == car || !IsActive(other)) continue;
                    var d3 = other.Status.Position - car.Status.Position;
                    var d = new Vector2(d3.X, d3.Z);
                    float fwd = moving ? Vector2.Dot(d, fwd2) : 0f;
                    float lat = moving ? d.X * fwd2.Y - d.Y * fwd2.X : 0f;
                    int idx = other.SessionId;

                    if (moving && st.Prev.TryGetValue(idx, out var prev)
                        && prev.fwd > 0 && fwd <= 0 && prev.fwd < 15 && fwd > -15)
                    {
                        float t = prev.fwd / (prev.fwd - fwd);
                        float latCross = prev.lat + (lat - prev.lat) * t;
                        float gap = MathF.Abs(latCross) - _config.CarWidthSum;

                        var ov = other.Status.Velocity;
                        float oSpeed = ov.Length() * 3.6f;
                        var ovh = new Vector2(ov.X, ov.Z);
                        bool sameDir = oSpeed < 5f ||
                            (ovh.LengthSquared() > 0.01f && Vector2.Dot(Vector2.Normalize(ovh), fwd2) > 0.6f);
                        float rel = speed - oSpeed;
                        bool cooled = !st.LastPass.TryGetValue(idx, out var last) || (st.Clock - last) > 2;

                        bool ok = sameDir && cooled && speed >= _config.MinSpeed && rel >= _config.MinRelativeSpeed
                                  && gap > -0.3f && gap <= MathF.Max(_config.MaxGap, _config.CloseGap);
                        if (_config.Debug && MathF.Abs(latCross) < 8f)
                        {
                            Log.Information(
                                "SonmezKombo DEBUG {Name} -> slot {Slot}: gap={Gap:F2}m hiz={Speed:F0} digerHiz={OSpeed:F0} fark={Rel:F0} ayniYon={Same} bekleme={Cooled} => {Result}",
                                client.Name, idx, gap, speed, oSpeed, rel, sameDir, cooled, ok ? "PUAN" : "YOK");
                        }

                        if (ok)
                        {
                            st.LastPass[idx] = st.Clock;
                            GiveScore(client, st, gap, speed);
                        }
                    }
                    st.Prev[idx] = (fwd, lat);
                }
            }
        }
    }

    // Puan ver (st kilitliyken cagrilmali). gap = iki arac arasindaki bosluk (m), speed = oyuncu hizi (km/h)
    private void GiveScore(ACTcpClient client, PlayerState st, float gap, float speed)
    {
        st.Combo++;
        st.ComboLeft = _config.ComboTimeout;
        float closeness = 1f + Math.Clamp((_config.CloseGap - MathF.Max(gap, 0f)) / _config.CloseGap, 0f, 1f) * 2f;

        // Coklu makas: kisa surede birden fazla arac (farkli seritler) gecildiyse bonus
        st.MultiCount = st.MultiLeft > 0 ? st.MultiCount + 1 : 1;
        st.MultiLeft = _config.MultiWindow;
        int multi = Math.Clamp(st.MultiCount, 1, Math.Max(1, _config.MaxMulti));
        float multiMult = 1f + (multi - 1) * _config.MultiBonus;

        st.Prox = closeness;
        st.ProxLeft = 2.0;
        double pts = _config.BasePoints * closeness * TotalMult(speed, st.Combo) * multiMult;
        st.Score += pts; // anlik puan (kazada / canlar bitince sifirlanir)
        // Toplam puan: kazandigin her puan buraya eklenir, kazada SIFIRLANMAZ
        long add = Math.Max(1, (long)pts);
        st.Best = (long)Math.Min((double)st.Best + add, MaxScore);
        _store.Add(st.Key, client.Name ?? "?", add);
        client.SendPacket(new KomboPopup { Points = (int)Math.Min(add, int.MaxValue), Kind = 0, Multi = multi });
    }

    private bool ServerMode => string.Equals(_config.PassMode, "server", StringComparison.OrdinalIgnoreCase);

    // Istemci (Lua) makas bildirimi: HTTP POST /sonmezkombo/pass
    // Govde: 1. satir slot no, 2. satir oyuncu adi, sonraki satirlar "yanMesafe;hiz;digerHiz"
    public int ReportPasses(string body)
    {
        if (_config.Debug) Log.Information("SonmezKombo DEBUG rapor geldi: {Len} bayt, mod={Mode}", body.Length, _config.PassMode);
        if (ServerMode) return 0;
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3) return 0;
        if (!int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid)) sid = -1;
        var name = lines[1];

        ACTcpClient? client = null;
        foreach (var car in _entryCarManager.EntryCars)
        {
            var c = car.Client;
            if (c == null || !c.HasSentFirstUpdate) continue;
            if (c.SessionId == sid && string.Equals((c.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                client = c;
                break;
            }
        }
        if (client == null)
        {
            foreach (var car in _entryCarManager.EntryCars)
            {
                var c = car.Client;
                if (c == null || !c.HasSentFirstUpdate) continue;
                if (string.Equals((c.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    client = c;
                    break;
                }
            }
        }
        if (client == null)
        {
            if (_config.Debug) Log.Information("SonmezKombo DEBUG rapor: oyuncu bulunamadi (slot {Sid}, isim {Name})", sid, name);
            return 0;
        }

        var st = GetState(client);
        int given = 0;
        for (int i = 2; i < lines.Length; i++)
        {
            var parts = lines[i].Replace(',', '.').Split(';');
            if (parts.Length < 3) continue;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)) continue;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var oSpeed)) continue;
            if (float.IsNaN(lat) || float.IsNaN(speed) || float.IsNaN(oSpeed) || speed > 700f) continue;

            float gap = MathF.Abs(lat) - _config.CarWidthSum;
            float rel = speed - oSpeed;
            bool ok = speed >= _config.MinSpeed && rel >= _config.MinRelativeSpeed
                      && gap > -0.3f && gap <= MathF.Max(_config.MaxGap, _config.CloseGap);
            if (_config.Debug)
            {
                Log.Information("SonmezKombo DEBUG rapor {Name}: gap={Gap:F2}m hiz={Speed:F0} digerHiz={OSpeed:F0} fark={Rel:F0} => {Result}",
                    client.Name, gap, speed, oSpeed, rel, ok ? "PUAN" : "YOK");
            }
            if (!ok) continue;
            lock (st)
            {
                GiveScore(client, st, gap, speed);
            }
            given++;
        }
        return given;
    }

    private const double MaxScore = 9e15; // Lua double hassasiyet siniri

    private static (int hi, int lo) Split(long v)
    {
        if (v < 0) v = 0;
        return ((int)(v / 1_000_000_000L), (int)(v % 1_000_000_000L));
    }

    private static float SpeedMult(float speed) => MathF.Max(1f, speed / 25f);
    private static float ComboMult(int combo) => 1f + MathF.Min(combo, 500) * 0.004f;
    private static float TotalMult(float speed, int combo) => SpeedMult(speed) * ComboMult(combo);

    // Canli puan listesi: her oyuncuya ilk 5 (anlik puana gore); oyuncu ilk 5'te degilse son satir kendisi. ~1 sn'de bir.
    private void SendLive()
    {
        var rows = new List<(ACTcpClient Client, long Score)>();
        foreach (var car in _entryCarManager.EntryCars)
        {
            var client = car.Client;
            if (client == null || !client.HasSentFirstUpdate) continue;
            var st = GetState(client);
            long score;
            lock (st) score = (long)Math.Min(st.Score, MaxScore);
            rows.Add((client, score));
        }
        if (rows.Count == 0) return;

        var sorted = rows.OrderByDescending(r => r.Score).ThenBy(r => r.Client.SessionId).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int myRank = i + 1;
            var pick = new List<(ACTcpClient Client, long Score)>();
            if (myRank <= 5)
            {
                pick.AddRange(sorted.Take(5));
            }
            else
            {
                pick.AddRange(sorted.Take(4));
                pick.Add(sorted[i]);
            }

            var ids = new int[5];
            var his = new int[5];
            var los = new int[5];
            for (int k = 0; k < 5; k++)
            {
                if (k < pick.Count)
                {
                    ids[k] = pick[k].Client.SessionId;
                    (his[k], los[k]) = Split(pick[k].Score);
                }
                else
                {
                    ids[k] = -1;
                }
            }

            var pkt = new KomboLive
            {
                Count = pick.Count,
                MyRank = myRank,
                Id1 = ids[0], Hi1 = his[0], Lo1 = los[0],
                Id2 = ids[1], Hi2 = his[1], Lo2 = los[1],
                Id3 = ids[2], Hi3 = his[2], Lo3 = los[2],
                Id4 = ids[3], Hi4 = his[3], Lo4 = los[3],
                Id5 = ids[4], Hi5 = his[4], Lo5 = los[4]
            };
            try
            {
                sorted[i].Client.SendPacket(pkt);
            }
            catch (Exception ex)
            {
                if (_config.Debug) Log.Information(ex, "SonmezKombo: canli liste gonderilemedi ({Name})", sorted[i].Client.Name);
            }
        }
    }

    private void SendStates()
    {
        foreach (var car in _entryCarManager.EntryCars)
        {
            var client = car.Client;
            if (client == null || !client.HasSentFirstUpdate) continue;
            var st = GetState(client);
            float speed = car.Status.Velocity.Length() * 3.6f;
            KomboState pkt;
            lock (st)
            {
                // Ortak skor dosyasindan gelen (baska sunucularda kazanilan) toplam bu sunucudakinden yuksekse onu al
                var storeBest = _store.GetBest(st.Key);
                if (storeBest > st.Best) st.Best = storeBest;
                // Kafe geneli toplam (hub) daha yuksekse onu goster
                var hubBest = _hub.Available ? _hub.GetBest(st.Key) : 0;
                if (hubBest > st.Best) st.Best = hubBest;
                var (sh, sl) = Split((long)Math.Min(st.Score, MaxScore));
                var (bh, bl) = Split(st.Best);
                pkt = new KomboState
                {
                    ScoreHi = sh,
                    ScoreLo = sl,
                    BestHi = bh,
                    BestLo = bl,
                    Combo = st.Combo,
                    Lives = st.Lives,
                    Time = (int)st.Time,
                    SpeedMult = SpeedMult(speed),
                    TotalMult = TotalMult(speed, st.Combo) * st.Prox,
                    ComboLeft = (float)Math.Clamp(st.ComboLeft / _config.ComboTimeout, 0, 1),
                    BarMax = _config.BarMaxScore,
                    Rank = _hub.Available ? _hub.Rank(st.Key, st.Best) : _store.Rank(st.Key),
                    Prox = st.Prox
                };
            }
            client.SendPacket(pkt);
        }
    }
}
