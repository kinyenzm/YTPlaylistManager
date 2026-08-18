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

public sealed class QuotaTracker(IConfiguration cfg)
    : JsonFileStore(cfg, "quota.json")
{
    private readonly int _limit = int.TryParse(cfg["YouTube:DailyQuota"], out var q) ? q : 10000;

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
        lock (Sync)
        {
            var s = Load();
            return (s.Used, _limit, s.Date);
        }
    }

    /// <summary>Suma unidades gastadas (insert/delete=50, lista/items=1 por página).</summary>
    public void Add(int units)
    {
        if (units <= 0) return;
        lock (Sync)
        {
            var s = Load();
            s.Used += units;
            Save(s);
        }
    }

    /// <summary>
    /// YouTube respondió quotaExceeded: el contador local es solo una estimación y
    /// otras apps del mismo proyecto pudieron gastar cuota, así que se fija Used al
    /// límite para que el restante mostrado quede en cero hasta el reinicio diario.
    /// </summary>
    public void MarkExhausted()
    {
        lock (Sync)
        {
            var s = Load();
            if (s.Used >= _limit) return;
            s.Used = _limit;
            Save(s);
        }
    }

    /// <summary>True si la excepción de Google es por cuota/límite de tasa (403).</summary>
    public static bool IsQuotaError(Google.GoogleApiException ex)
    {
        if (ex.HttpStatusCode != System.Net.HttpStatusCode.Forbidden) return false;
        var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason;
        return reason is "quotaExceeded" or "rateLimitExceeded" or "dailyLimitExceeded"
            || (ex.Message?.Contains("quota", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private QuotaState Load()
    {
        var today = Today();
        var s = Read<QuotaState>() ?? new QuotaState();
        if (s.Date != today)   // cambió el día → reinicio automático
        {
            s = new QuotaState { Date = today, Used = 0 };
            Save(s);
        }
        return s;
    }

    private void Save(QuotaState s) => Write(s, indented: false);
}
