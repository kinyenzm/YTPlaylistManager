using System.Text.Json;

namespace YTPlaylistManager.Server.Services;

public sealed class ArchivedPlaylistEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public DateTime ArchivedAtUtc { get; init; }
    public required string MergedIntoPlaylistId { get; init; }
    public required string MergedIntoPlaylistTitle { get; init; }
    public int SongsCount { get; init; }
}

/// <summary>
/// Persiste el registro de playlists "archivadas" localmente (porque se
/// consolidaron en otra). El concepto es local: no borra de YouTube.
/// </summary>
public class ArchivedPlaylistsStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public ArchivedPlaylistsStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "archived-playlists.json");
    }

    public List<ArchivedPlaylistEntry> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<ArchivedPlaylistEntry>>(File.ReadAllText(_path)) ?? [];
        }
    }

    public bool IsArchived(string playlistId) =>
        LoadAll().Any(e => e.Id == playlistId);

    public void Add(IEnumerable<ArchivedPlaylistEntry> entries)
    {
        lock (_lock)
        {
            var list = LoadAll();
            foreach (var e in entries)
            {
                list.RemoveAll(x => x.Id == e.Id);
                list.Add(e);
            }
            File.WriteAllText(_path,
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Remove(string playlistId)
    {
        lock (_lock)
        {
            var list = LoadAll();
            list.RemoveAll(x => x.Id == playlistId);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
