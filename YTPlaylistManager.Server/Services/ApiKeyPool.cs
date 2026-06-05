namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Pool de API keys (una por proyecto de Google Cloud) para LECTURAS de contenido público.
/// Permite sumar la cuota de varios proyectos al leer items. Solo se activa con el flag
/// oculto <c>Google:QuotaPooling</c>. Las API keys NO requieren login/consent (solo leen público).
/// </summary>
public sealed class ApiKeyPool
{
    public IReadOnlyList<string> Keys { get; }
    public bool Enabled { get; }

    public ApiKeyPool(IConfiguration cfg)
    {
        var keys = new List<string>();

        // Key primaria (la de siempre).
        var primary = cfg["Google:key"];
        if (!string.IsNullOrWhiteSpace(primary)) keys.Add(primary);

        // Keys extra de otros proyectos (pooling).
        var extra = cfg.GetSection("Google:ApiKeys").Get<string[]>();
        if (extra is not null)
            keys.AddRange(extra.Where(k => !string.IsNullOrWhiteSpace(k)));

        Keys = keys;
        Enabled = cfg.GetValue("Google:QuotaPooling", false) && keys.Count > 0;
    }
}
