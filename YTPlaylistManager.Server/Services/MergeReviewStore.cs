using System.Text.Json;
using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Cola de planes de merge en revisión. Se persiste en JSON local — sobrevive
/// reinicios. El usuario revisa cada plan (qué canciones se agregarían, qué
/// playlists se archivarían) y decide aplicar o descartar.
/// </summary>
public class MergeReviewStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public MergeReviewStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "merge-reviews.json");
    }

    public List<MergeReviewPlan> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<MergeReviewPlan>>(File.ReadAllText(_path)) ?? [];
        }
    }

}
