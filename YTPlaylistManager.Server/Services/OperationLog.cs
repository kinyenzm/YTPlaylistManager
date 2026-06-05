using System.Text.Json;
using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Log persistente en JSON (append). Útil para auditar qué hizo la herramienta sobre la cuenta.
/// </summary>
public class OperationLog
{
    private readonly string _path;
    private readonly object _lock = new();

    public OperationLog(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "operations.json");
    }

    public void Add(string operation, string details)
    {
        lock (_lock)
        {
            var list = LoadAll();
            list.Add(new OperationLogEntry(DateTime.UtcNow, operation, details));
            File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public List<OperationLogEntry> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return new();
            var raw = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<OperationLogEntry>>(raw) ?? new();
        }
    }
}
