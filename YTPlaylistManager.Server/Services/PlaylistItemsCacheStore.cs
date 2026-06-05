using System.Text.Json;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Caché en JSON de los items por playlist, por cuenta (UserKey). Evita re-leer de YouTube
/// (cuota) en cada análisis de duplicados/repetidas. Los escaneos parciales se acumulan:
/// lo que se lee una vez queda guardado y se reutiliza.
/// </summary>
public class PlaylistItemsCacheStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public PlaylistItemsCacheStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "items-cache.json");
    }

    public List<PlaylistItemDto>? Load(string userKey, string playlistId)
    {
        lock (_lock)
        {
            var all = LoadAllUnlocked();
            if (all.TryGetValue(userKey, out var byPlaylist) &&
                byPlaylist.TryGetValue(playlistId, out var entry))
                return entry.Items;

            // Fallback: el playlistId es único en YouTube. Si quedó cacheado bajo otra
            // clave (p. ej. el token de Google cambió y la UserKey se recalculó), igual
            // sirve — así lo ya leído no se vuelve a pedir a YouTube (0 cuota).
            foreach (var other in all.Values)
                if (other.TryGetValue(playlistId, out var e2))
                    return e2.Items;

            return null;
        }
    }

    public void Save(string userKey, string playlistId, List<PlaylistItemDto> items)
    {
        lock (_lock)
        {
            var all = LoadAllUnlocked();
            if (!all.TryGetValue(userKey, out var byPlaylist))
            {
                byPlaylist = [];
                all[userKey] = byPlaylist;
            }
            byPlaylist[playlistId] = new CachedItems { CachedAtUtc = DateTime.UtcNow, Items = items };
            SaveAll(all);
        }
    }

    /// <summary>Invalida la caché de una playlist (tras escrituras que la cambian).</summary>
    public void Invalidate(string userKey, string playlistId)
    {
        lock (_lock)
        {
            var all = LoadAllUnlocked();
            bool changed = false;
            foreach (var byPlaylist in all.Values)   // quitar de todas las claves (incl. copias huérfanas)
                if (byPlaylist.Remove(playlistId)) changed = true;
            if (changed) SaveAll(all);
        }
    }

    private Dictionary<string, Dictionary<string, CachedItems>> LoadAllUnlocked()
    {
        if (!File.Exists(_path)) return [];
        var raw = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CachedItems>>>(raw) ?? [];
    }

    private void SaveAll(Dictionary<string, Dictionary<string, CachedItems>> all)
        => File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = false }));
}

public sealed class CachedItems
{
    public DateTime CachedAtUtc { get; init; }
    public required List<PlaylistItemDto> Items { get; init; }
}
