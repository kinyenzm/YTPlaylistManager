using System.Text.Json;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Última modificación LOCAL por playlist (staging de canciones, merges, limpiezas).
/// YouTube no expone "última modificación" de una playlist; este registro la
/// aproxima con las acciones hechas desde la app. playlistId → fecha UTC.
/// </summary>
public class PlaylistTouchStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public PlaylistTouchStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "playlist-touch.json");
    }

    public Dictionary<string, DateTime> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path)) ?? [];
        }
    }

    public void Touch(IEnumerable<string> playlistIds)
    {
        lock (_lock)
        {
            var map = LoadAll();
            var now = DateTime.UtcNow;
            foreach (var id in playlistIds.Where(id => !string.IsNullOrEmpty(id)))
                map[id] = now;
            File.WriteAllText(_path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Touch(string playlistId) => Touch([playlistId]);
}
