using System.Text.Json;
using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Uniones aplicadas en local pendientes de subir a YouTube. Persistido en JSON
/// local (sobrevive reinicios). Keyed por cuenta (UserKey) dentro de cada registro.
/// </summary>
public class PendingUploadStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public PendingUploadStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "pending-uploads.json");
    }

    public List<PendingUpload> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<PendingUpload>>(File.ReadAllText(_path)) ?? [];
        }
    }

    public List<PendingUpload> LoadForUser(string userKey) =>
        LoadAll().Where(p => p.UserKey == userKey).OrderByDescending(p => p.CreatedAtUtc).ToList();

    public PendingUpload? Get(string id) => LoadAll().FirstOrDefault(p => p.Id == id);

    public PendingUpload Add(PendingUpload plan)
    {
        lock (_lock)
        {
            var list = LoadAll();
            list.Add(plan);
            Save(list);
            return plan;
        }
    }

    /// <summary>Reemplaza (o agrega) un plan por su Id — usado tras subir parcialmente.</summary>
    public void Replace(PendingUpload plan)
    {
        lock (_lock)
        {
            var list = LoadAll();
            list.RemoveAll(p => p.Id == plan.Id);
            list.Add(plan);
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

    private void Save(List<PendingUpload> list) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
}
