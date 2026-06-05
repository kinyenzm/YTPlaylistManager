namespace YTPlaylistManager.Server.Domain.Entities;

/// <summary>
/// Unión que quedó a medias (típicamente por cuota agotada). Guarda los videoIds que
/// faltan agregar al destino, para reanudarla cuando se reponga la cuota.
/// </summary>
public sealed class PendingMerge
{
    public required string Id { get; init; }
    public required string TargetPlaylistId { get; init; }
    public required string TargetPlaylistTitle { get; init; }
    public required List<string> PendingVideoIds { get; set; }
    public DateTime CreatedAtUtc { get; init; }
}
