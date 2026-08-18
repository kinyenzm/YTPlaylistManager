namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Última modificación LOCAL por playlist (staging de canciones, merges, limpiezas).
/// YouTube no expone "última modificación" de una playlist; este registro la
/// aproxima con las acciones hechas desde la app. playlistId → fecha UTC.
/// </summary>
public sealed class PlaylistTouchStore(IConfiguration cfg)
    : JsonFileStore(cfg, "playlist-touch.json")
{
    public Dictionary<string, DateTime> LoadAll()
    {
        lock (Sync) return Read<Dictionary<string, DateTime>>() ?? [];
    }

    public void Touch(IEnumerable<string> playlistIds)
    {
        lock (Sync)
        {
            var map = Read<Dictionary<string, DateTime>>() ?? [];
            var now = DateTime.UtcNow;
            foreach (var id in playlistIds.Where(id => !string.IsNullOrEmpty(id)))
                map[id] = now;
            Write(map);
        }
    }

    public void Touch(string playlistId) => Touch([playlistId]);
}
