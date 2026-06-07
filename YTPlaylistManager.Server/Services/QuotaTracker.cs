using System.Text.Json;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Contador estimado de unidades de cuota de YouTube usadas hoy. Se reinicia solo
/// cada día (medianoche hora Pacífico, igual que YouTube). Persistido en JSON.
/// Es una estimación: cuenta lo que ESTA app gasta (no lo que gasten otras apps
/// del mismo proyecto de Google Cloud).
/// </summary>
public sealed class QuotaState
{
    public string Date { get; set; } = "";   // yyyy-MM-dd (hora Pacífico)
    public int Used { get; set; }
}

public class QuotaTracker
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly int _limit;

    public QuotaTracker(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "quota.json");
        _limit = int.TryParse(cfg["YouTube:DailyQuota"], out var q) ? q : 10000;
    }

    private static string Today()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).ToString("yyyy-MM-dd");
        }
        catch
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd");
        }
    }

    public (int Used, int Limit, string Date) Get()
    {
        lock (_lock)
        {
            var s = Load();
            return (s.Used, _limit, s.Date);
        }
    }

    /// <summary>Suma unidades gastadas (insert/delete=50, lista/items=1 por página).</summary>
    public void Add(int units)
    {
        if (units <= 0) return;
        lock (_lock)
        {
            var s = Load();
            s.Used += units;
            Save(s);
        }
    }

    private QuotaState Load()
    {
        var today = Today();
        QuotaState s = File.Exists(_path)
            ? (JsonSerializer.Deserialize<QuotaState>(File.ReadAllText(_path)) ?? new QuotaState())
            : new QuotaState();
        if (s.Date != today)   // cambió el día → reinicio automático
        {
            s = new QuotaState { Date = today, Used = 0 };
            Save(s);
        }
        return s;
    }

    private void Save(QuotaState s) => File.WriteAllText(_path, JsonSerializer.Serialize(s));
}
