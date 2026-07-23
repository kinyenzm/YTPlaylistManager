using System.Text.Json;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Caché en JSON de la lista de playlists, por cuenta (UserKey). Permite recargar
/// con la misma cuenta sin volver a pedir a YouTube (0 cuota) y servir de fallback
/// cuando la API falla (cuota agotada/red).
/// </summary>
public class PlaylistCacheStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public PlaylistCacheStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "playlist-cache.json");
    }

    public PlaylistCache? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<PlaylistCache>(File.ReadAllText(_path));
        }
    }

    public void Save(PlaylistCache cache)
    {
        lock (_lock)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    /// <summary>Re-etiqueta la caché a la clave nueva (migración de UserKey).</summary>
    public void MigrateToKey(string newKey)
    {
        lock (_lock)
        {
            var cache = Load();
            if (cache is null || cache.UserKey == newKey) return;
            Save(new PlaylistCache
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
