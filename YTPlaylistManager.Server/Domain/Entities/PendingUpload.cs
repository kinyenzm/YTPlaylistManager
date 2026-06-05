namespace YTPlaylistManager.Server.Domain.Entities;

/// <summary>
/// Una unión aplicada en local (caché) que todavía NO se subió a YouTube.
/// Guarda exactamente las canciones que faltan agregar en la lista destino
/// para que el usuario las revise y las suba cuando quiera.
/// </summary>
public sealed class PendingUpload
{
    public string Id { get; set; } = "";
    public string UserKey { get; set; } = "";
    public string TargetPlaylistId { get; set; } = "";
    public string TargetPlaylistTitle { get; set; } = "";
    public List<PendingUploadItem> Items { get; set; } = [];
    public List<PendingSource> Sources { get; set; } = [];   // listas origen a borrar al subir
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class PendingSource
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class PendingUploadItem
{
    public string LocalItemId { get; set; } = "";   // id sintético en la caché local (para revertir al descartar)
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? ChannelTitle { get; set; }
    public string? ThumbnailUrl { get; set; }
    public List<string> FromPlaylists { get; set; } = [];   // listas origen donde aparece (sin repetir)
}
