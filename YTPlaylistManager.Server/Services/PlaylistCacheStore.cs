using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Caché en JSON de la lista de playlists, por cuenta (UserKey). Permite recargar
/// con la misma cuenta sin volver a pedir a YouTube (0 cuota) y servir de fallback
/// cuando la API falla (cuota agotada/red).
/// </summary>
public sealed class PlaylistCacheStore(IConfiguration cfg)
    : JsonFileStore(cfg, "playlist-cache.json")
{
    public PlaylistCache? Load()
    {
        lock (Sync) return Read<PlaylistCache>();
    }

    public void Save(PlaylistCache cache)
    {
        lock (Sync) Write(cache);
    }

    /// <summary>Re-etiqueta la caché a la clave nueva (migración de UserKey).</summary>
    public void MigrateToKey(string newKey)
    {
        lock (Sync)
        {
            var cache = Read<PlaylistCache>();
            if (cache is null || cache.UserKey == newKey) return;
            Write(new PlaylistCache
            {
                UserKey = newKey,
                CachedAtUtc = cache.CachedAtUtc,
                Playlists = cache.Playlists,
            });
        }
    }
}

public sealed class PlaylistCache
{
    public required string UserKey { get; init; }
    public DateTime CachedAtUtc { get; init; }
    public required List<PlaylistDto> Playlists { get; init; }
}
