using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Almacén simple en JSON del token OAuth del usuario.
/// Para una herramienta personal/local es suficiente. NO usar así en producción multi-usuario.
/// </summary>
public sealed class GoogleTokenStore(IConfiguration cfg)
    : JsonFileStore(cfg, "google-token.json")
{
    public GoogleTokenData? Load()
    {
        lock (Sync) return Read<GoogleTokenData>();
    }

    public void Save(GoogleTokenData data)
    {
        lock (Sync) Write(data);
    }

    public void Clear()
    {
        lock (Sync) DeleteFile();
    }
}
