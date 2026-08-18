using System.Text.Json;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Caché en JSON de los items por playlist, por cuenta (UserKey). Evita re-leer de YouTube
/// (cuota) en cada análisis de duplicados/repetidas. Los escaneos parciales se acumulan:
/// lo que se lee una vez queda guardado y se reutiliza.
///
/// El documento deserializado se conserva en memoria (el archivo ronda 1,4 MB y algunos
/// flujos hacen un Load por playlist: sin esto, una sola petición de ubicaciones
/// deserializaba el archivo entero ~50 veces). Toda escritura pasa por SaveAll, que
/// mantiene memoria y disco en sincronía. Las listas devueltas se comparten con la
/// caché: tratarlas como de solo lectura (los DTOs son records inmutables).
/// </summary>
public sealed class PlaylistItemsCacheStore(IConfiguration cfg)
    : JsonFileStore(cfg, "items-cache.json")
{
    private Dictionary<string, Dictionary<string, CachedItems>>? _doc;

    public List<PlaylistItemDto>? Load(string userKey, string playlistId)
    {
        lock (Sync)
        {
            var all = Doc();
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
        lock (Sync)
        {
            var all = Doc();
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
        lock (Sync)
        {
            var all = Doc();
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
        lock (Sync)
        {
            var all = Doc();
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
        lock (Sync)
        {
            var merged = new Dictionary<string, CachedItems>();
            foreach (var byPlaylist in Doc().Values)
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
        var bak = FilePath + ".bak";
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

    private Dictionary<string, Dictionary<string, CachedItems>> Doc()
        => _doc ??= Read<Dictionary<string, Dictionary<string, CachedItems>>>() ?? [];

    private void SaveAll(Dictionary<string, Dictionary<string, CachedItems>> all)
    {
        _doc = all;
        Write(all, indented: false);
    }
}

public sealed class CachedItems
{
    public DateTime CachedAtUtc { get; init; }
    public required List<PlaylistItemDto> Items { get; init; }
}
