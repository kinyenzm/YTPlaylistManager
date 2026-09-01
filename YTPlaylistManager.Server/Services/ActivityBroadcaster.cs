namespace YTPlaylistManager.Server.Services;

/// <summary>Evento de actividad real en YouTube (insertar/quitar canción, borrar lista).</summary>
public sealed record ActivityEvent(string Type, string Title, string Playlist, string VideoId, DateTime At);

/// <summary>
/// Registro persistido de la actividad real en YouTube. Cada operación de
/// escritura publica un evento; se guarda en disco (últimos N) y se consulta
/// desde Historial → pestaña Actividad.
/// </summary>
public sealed class ActivityBroadcaster : JsonFileStore
{
    private readonly List<ActivityEvent> _log;   // más antiguo primero; siempre en memoria
    private const int LogMax = 1000;

    public ActivityBroadcaster(IConfiguration cfg) : base(cfg, "activity-log.json")
    {
        _log = Read<List<ActivityEvent>>() ?? [];
    }

    /// <summary>Relee el log del disco (los archivos cambiaron por fuera: import de respaldo).</summary>
    public void Reload()
    {
        lock (Sync)
        {
            _log.Clear();
            _log.AddRange(Read<List<ActivityEvent>>() ?? []);
        }
    }

    /// <summary>Historial persistido, más reciente primero.</summary>
    public List<ActivityEvent> History(int max = 200)
    {
        lock (Sync)
        {
            var n = Math.Clamp(max, 0, _log.Count);
            var slice = _log.GetRange(_log.Count - n, n);
            slice.Reverse();
            return slice;
        }
    }

    public void Publish(ActivityEvent e)
    {
        lock (Sync)
        {
            _log.Add(e);
            while (_log.Count > LogMax) _log.RemoveAt(0);
            try { Write(_log, indented: false); }
            catch { /* best-effort: un fallo de disco no debe romper la subida */ }
        }
    }
}
