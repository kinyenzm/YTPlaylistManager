namespace YTPlaylistManager.Server.Domain.Entities;

/// <summary>
/// Plan de merge guardado en la cola de revisión. Se crea al previsualizar un
/// merge (wizard) y se aplica al caché local cuando el usuario confirma.
/// No toca YouTube: queda como "draft" hasta que se aplica.
/// </summary>
public sealed class MergeReviewPlan
{
    public required string Id { get; init; }
    public required string TargetPlaylistId { get; init; }
    public required string TargetPlaylistTitle { get; init; }
    public required List<ReviewSource> Sources { get; init; }
    public required List<ReviewItem> NewSongs { get; init; }
    public required List<ReviewItem> DuplicateSongs { get; init; }
    public int EstimatedQuotaCost { get; init; }
    public bool DeleteSources { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class ReviewSource
{
    public required string PlaylistId { get; init; }
    public required string Title { get; init; }
    public int ItemCount { get; init; }
}

public sealed class ReviewItem
{
    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public string? ChannelTitle { get; init; }
    public required string SourcePlaylistId { get; init; }
    public required string SourcePlaylistTitle { get; init; }
}
