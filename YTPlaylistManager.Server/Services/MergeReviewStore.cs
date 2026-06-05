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

    public MergeReviewPlan? Get(string id) => LoadAll().FirstOrDefault(p => p.Id == id);

    public MergeReviewPlan Add(MergeReviewPlan plan)
    {
        lock (_lock)
        {
            var list = LoadAll();
            list.Add(plan);
            Save(list);
            return plan;
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var list = LoadAll();
            list.RemoveAll(p => p.Id == id);
            Save(list);
        }
    }

    public void Clear() => File.Delete(_path);

    private void Save(List<MergeReviewPlan> list)
    {
        File.WriteAllText(_path,
            JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }
}
