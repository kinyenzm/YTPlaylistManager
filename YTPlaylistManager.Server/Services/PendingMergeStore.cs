using System.Text.Json;
using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Almacén JSON de uniones pendientes (las que quedaron a medias por cuota).
/// Suficiente para una herramienta personal local.
/// </summary>
public class PendingMergeStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public PendingMergeStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "pending-merges.json");
    }

    public List<PendingMerge> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            var raw = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<PendingMerge>>(raw) ?? [];
        }
    }

    public PendingMerge? Get(string id)
    {
        lock (_lock)
        {
            return LoadAllUnlocked().FirstOrDefault(p => p.Id == id);
        }
    }

    public PendingMerge Add(string targetPlaylistId, string targetPlaylistTitle, List<string> pendingVideoIds)
    {
        lock (_lock)
        {
            var list = LoadAllUnlocked();
            var pm = new PendingMerge
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                TargetPlaylistId = targetPlaylistId,
                TargetPlaylistTitle = targetPlaylistTitle,
                PendingVideoIds = pendingVideoIds,
                CreatedAtUtc = DateTime.UtcNow
            };
            list.Add(pm);
            Save(list);
            return pm;
        }
    }

    public void UpdatePending(string id, List<string> remainingVideoIds)
    {
        lock (_lock)
        {
            var list = LoadAllUnlocked();
            var pm = list.FirstOrDefault(p => p.Id == id);
            if (pm is null) return;
            pm.PendingVideoIds = remainingVideoIds;
            Save(list);
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var list = LoadAllUnlocked();
            list.RemoveAll(p => p.Id == id);
            Save(list);
        }
    }

    private List<PendingMerge> LoadAllUnlocked()
    {
        if (!File.Exists(_path)) return [];
        var raw = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<List<PendingMerge>>(raw) ?? [];
    }

    private void Save(List<PendingMerge> list)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }
}
