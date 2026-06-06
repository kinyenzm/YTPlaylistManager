using System.Text.Json;
using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Reasignaciones de canciones aplicadas en local pendientes de subir a YouTube.
/// Persistido en JSON (sobrevive reinicios → la subida es reanudable).
/// </summary>
public class PendingSongMoveStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public PendingSongMoveStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "pending-song-moves.json");
    }

    public List<PendingSongMove> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<PendingSongMove>>(File.ReadAllText(_path)) ?? [];
        }
    }

    public List<PendingSongMove> LoadForUser(string userKey) =>
        LoadAll().Where(p => p.UserKey == userKey).OrderByDescending(p => p.CreatedAtUtc).ToList();

    public PendingSongMove? Get(string id) => LoadAll().FirstOrDefault(p => p.Id == id);

    public PendingSongMove Add(PendingSongMove move)
    {
        lock (_lock)
        {
            var list = LoadAll();
            list.Add(move);
            Save(list);
            return move;
        }
    }

    public void Replace(PendingSongMove move)
    {
        lock (_lock)
        {
            var list = LoadAll();
            list.RemoveAll(p => p.Id == move.Id);
            list.Add(move);
            Save(list);
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

    private void Save(List<PendingSongMove> list) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
}
