using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Lectura de playlists y ciclo de unión. Duplicados, recuperación y
/// reasignaciones tienen servicios propios (DuplicateService, RecoveryService,
/// SongMoveService).
/// </summary>
public interface IYouTubeService
{
    Task<List<PlaylistDto>> GetMyPlaylistsAsync(CancellationToken ct = default, bool forceRefresh = false, bool includeArchived = false);
    Task<List<PlaylistItemDto>> GetPlaylistItemsAsync(string playlistId, CancellationToken ct = default, bool forceRefresh = false);
    List<PlaylistItemDto> GetCachedItems(string playlistId);
    Task<RefreshAllResultDto> RefreshAllAsync(CancellationToken ct = default);

    MergePreviewDto PreviewMerge(MergePreviewRequest req);
    MergePlaylistsResultDto MergePlaylists(MergePlaylistsRequest req);
    List<PendingUploadDto> GetPendingUploads();
    Task<UploadResultDto> UploadPendingAsync(string id, int? limit = null, CancellationToken ct = default);
    void DiscardPending(string id);

    List<PlaylistArchivedInfoDto> GetArchivedPlaylists();
}
