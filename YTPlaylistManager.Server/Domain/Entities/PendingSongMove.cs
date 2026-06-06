namespace YTPlaylistManager.Server.Domain.Entities;

/// <summary>
/// Reasignación de una canción a playlists, aplicada en local y pendiente de subir.
/// Guarda en qué listas hay que insertarla (AddTo) y de cuáles quitarla (RemoveFrom).
/// </summary>
public sealed class PendingSongMove
{
    public string Id { get; set; } = "";
    public string UserKey { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ChannelTitle { get; set; }
    public string? ThumbnailUrl { get; set; }
    public List<SongMoveTarget> AddTo { get; set; } = [];
    public List<SongMoveRemoval> RemoveFrom { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class SongMoveTarget
{
    public string PlaylistId { get; set; } = "";
    public string PlaylistTitle { get; set; } = "";
    public string LocalItemId { get; set; } = "";   // id sintético en la caché local (para revertir/sincronizar)
}

public sealed class SongMoveRemoval
{
    public string PlaylistId { get; set; } = "";
    public string PlaylistTitle { get; set; } = "";
    public string PlaylistItemId { get; set; } = ""; // id real en YouTube (para borrar)
}
