using System.Threading.Channels;

namespace YTPlaylistManager.Server.Services;

/// <summary>Evento de actividad real en YouTube (insertar/quitar canción, borrar lista).</summary>
public sealed record ActivityEvent(string Type, string Title, string Playlist, string VideoId, DateTime At);

/// <summary>
/// Difusor en memoria de eventos de actividad. Cada operación real en YouTube publica
/// un evento; los suscriptores (SSE) lo reciben en vivo. Guarda los últimos N para que
/// un suscriptor que llega tarde vea lo reciente.
/// </summary>
public class ActivityBroadcaster
{
    private readonly object _lock = new();
    private readonly List<Channel<ActivityEvent>> _subs = [];
    private readonly Queue<ActivityEvent> _recent = new();
    private const int RecentMax = 50;

    public ChannelReader<ActivityEvent> Subscribe(out Channel<ActivityEvent> ch)
    {
        ch = Channel.CreateUnbounded<ActivityEvent>();
        lock (_lock) _subs.Add(ch);
        return ch.Reader;
    }

    public void Unsubscribe(Channel<ActivityEvent> ch)
    {
        lock (_lock) _subs.Remove(ch);
        ch.Writer.TryComplete();
    }

    public List<ActivityEvent> Recent()
    {
        lock (_lock) return [.. _recent];
    }

    public void Publish(ActivityEvent e)
    {
        lock (_lock)
        {
            _recent.Enqueue(e);
            while (_recent.Count > RecentMax) _recent.Dequeue();
            foreach (var c in _subs) c.Writer.TryWrite(e);
        }
    }
}
