using System.Text.Json;

namespace YTPlaylistManager.Server.Services;

/// <summary>Evento de actividad real en YouTube (insertar/quitar canción, borrar lista).</summary>
public sealed record ActivityEvent(string Type, string Title, string Playlist, string VideoId, DateTime At);

/// <summary>
/// Registro persistido de la actividad real en YouTube. Cada operación de
/// escritura publica un evento; se guarda en disco (últimos N) y se consulta
/// desde Historial → pestaña Actividad.
/// </summary>
public class ActivityBroadcaster
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<ActivityEvent> _log;   // más antiguo primero
    private const int LogMax = 1000;

    public ActivityBroadcaster(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "activity-log.json");
        _log = File.Exists(_path)
            ? (JsonSerializer.Deserialize<List<ActivityEvent>>(File.ReadAllText(_path)) ?? [])
            : [];
    }

    /// <summary>Historial persistido, más reciente primero.</summary>
    public List<ActivityEvent> History(int max = 200)
    {
        lock (_lock)
        {
            var n = Math.Clamp(max, 0, _log.Count);
            var slice = _log.GetRange(_log.Count - n, n);
            slice.Reverse();
            return slice;
        }
    }

    public void Publish(ActivityEvent e)
    {
        lock (_lock)
        {
            _log.Add(e);
            while (_log.Count > LogMax) _log.RemoveAt(0);
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_log)); }
            catch { /* best-effort: un fallo de disco no debe romper la subida */ }
        }
    }
}
