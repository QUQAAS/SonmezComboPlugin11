using AssettoServer.Network.ClientMessages;

namespace SonmezKomboPlugin;

// Skorlar 2 milyari asabilsin diye hi/lo olarak gonderilir: skor = Hi * 1_000_000_000 + Lo

// Her oyuncuya ozel durum (saniyede ~3 kez)
[OnlineEvent(Key = "SonmezKomboState")]
public class KomboState : OnlineEvent<KomboState>
{
    [OnlineEventField(Name = "scoreHi")] public int ScoreHi;
    [OnlineEventField(Name = "scoreLo")] public int ScoreLo;
    [OnlineEventField(Name = "bestHi")] public int BestHi;
    [OnlineEventField(Name = "bestLo")] public int BestLo;
    [OnlineEventField(Name = "combo")] public int Combo;
    [OnlineEventField(Name = "lives")] public int Lives;
    [OnlineEventField(Name = "time")] public int Time;
    [OnlineEventField(Name = "speedMult")] public float SpeedMult;
    [OnlineEventField(Name = "totalMult")] public float TotalMult;
    [OnlineEventField(Name = "comboLeft")] public float ComboLeft;
    [OnlineEventField(Name = "barMax")] public int BarMax;
    [OnlineEventField(Name = "rank")] public int Rank; // sunucudaki siralama (0 = henuz puan yok)
    [OnlineEventField(Name = "prox")] public float Prox; // yakinlik carpani (son makas)
}

// Makas / kaza yazisi
[OnlineEvent(Key = "SonmezKomboPopup")]
public class KomboPopup : OnlineEvent<KomboPopup>
{
    [OnlineEventField(Name = "points")] public int Points;
    [OnlineEventField(Name = "kind")] public int Kind; // 0 = makas, 1 = kaza
    [OnlineEventField(Name = "multi")] public int Multi; // kac arac birden gecildi (1 = tekli)
}

// Canli puan listesi: sunucudaki ilk 5 oyuncu (anlik puana gore). Siralama alan adlarindan (n00..n16) gelir,
// Lua tarafindaki ac.OnlineEvent ile birebir ayni olmali. Id = oyuncunun slot numarasi (isim Lua'da bulunur).
// Oyuncu ilk 5'te degilse son satir oyuncunun kendisidir (MyRank > 5).
[OnlineEvent(Key = "SonmezKomboLive")]
public class KomboLive : OnlineEvent<KomboLive>
{
    [OnlineEventField(Name = "n00")] public int Count;
    [OnlineEventField(Name = "n01")] public int MyRank;
    [OnlineEventField(Name = "n02")] public int Id1;
    [OnlineEventField(Name = "n03")] public int Hi1;
    [OnlineEventField(Name = "n04")] public int Lo1;
    [OnlineEventField(Name = "n05")] public int Id2;
    [OnlineEventField(Name = "n06")] public int Hi2;
    [OnlineEventField(Name = "n07")] public int Lo2;
    [OnlineEventField(Name = "n08")] public int Id3;
    [OnlineEventField(Name = "n09")] public int Hi3;
    [OnlineEventField(Name = "n10")] public int Lo3;
    [OnlineEventField(Name = "n11")] public int Id4;
    [OnlineEventField(Name = "n12")] public int Hi4;
    [OnlineEventField(Name = "n13")] public int Lo4;
    [OnlineEventField(Name = "n14")] public int Id5;
    [OnlineEventField(Name = "n15")] public int Hi5;
    [OnlineEventField(Name = "n16")] public int Lo5;
}
