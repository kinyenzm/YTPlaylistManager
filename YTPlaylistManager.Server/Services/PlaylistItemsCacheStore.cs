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

    /// <summary>
    /// Fusiona TODAS las claves bajo <paramref name="newKey"/> (app single-user: toda
    /// clave preexistente pertenece al mismo humano). Ante playlistId repetido gana la
    /// entrada con más items; empate → la más reciente. Elimina las claves viejas.
    /// </summary>
    public void MigrateToKey(string newKey)
    {
        lock (_lock)
        {
            var all = LoadAllUnlocked();
            if (all.Count == 0 || (all.Count == 1 && all.ContainsKey(newKey))) return;

            var merged = all.TryGetValue(newKey, out var existing) ? existing : [];
            foreach (var (key, byPlaylist) in all)
            {
                if (key == newKey) continue;
                foreach (var (playlistId, entry) in byPlaylist)
                {
                    if (!merged.TryGetValue(playlistId, out var cur)
                        || entry.Items.Count > cur.Items.Count
                        || (entry.Items.Count == cur.Items.Count && entry.CachedAtUtc > cur.CachedAtUtc))
                    {
                        merged[playlistId] = entry;
                    }
                }
            }
            SaveAll(new Dictionary<string, Dictionary<string, CachedItems>> { [newKey] = merged });
        }
    }

    /// <summary>
    /// Snapshot playlistId → items fusionando todas las claves de usuario (ante repetido
    /// gana la entrada con más items). Incluye el respaldo .bak si existe: cachés viejas
    /// de listas ya borradas siguen contando como "canciones conocidas" para recuperación.
    /// </summary>
    public Dictionary<string, CachedItems> SnapshotAllPlaylists()
    {
        lock (_lock)
        {
            var merged = new Dictionary<string, CachedItems>();
            foreach (var byPlaylist in LoadAllUnlocked().Values)
                foreach (var (playlistId, entry) in byPlaylist)
                    if (!merged.TryGetValue(playlistId, out var cur) || entry.Items.Count > cur.Items.Count)
                        merged[playlistId] = entry;

            // El .bak solo aporta playlists que el caché vigente YA NO tiene (p. ej.
            // listas borradas cuya caché se invalidó); nunca pisa el estado actual.
            foreach (var byPlaylist in LoadBakUnlocked().Values)
                foreach (var (playlistId, entry) in byPlaylist)
                    if (!merged.ContainsKey(playlistId))
                        merged[playlistId] = entry;

            return merged;
        }
    }

    private Dictionary<string, Dictionary<string, CachedItems>> LoadBakUnlocked()
    {
        var bak = _path + ".bak";
        if (!File.Exists(bak)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CachedItems>>>(File.ReadAllText(bak)) ?? [];
        }
        catch
        {
            return [];   // respaldo corrupto → se ignora, no rompe el escaneo
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
