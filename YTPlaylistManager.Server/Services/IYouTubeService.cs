using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

public interface IYouTubeService
{
    Task<List<PlaylistDto>> GetMyPlaylistsAsync(CancellationToken ct = default, bool forceRefresh = false, bool includeArchived = false);
    Task<List<PlaylistItemDto>> GetPlaylistItemsAsync(string playlistId, CancellationToken ct = default, bool forceRefresh = false);
    Task<DuplicateReportDto> FindDuplicatesAsync(string playlistId, CancellationToken ct = default);
    Task<CrossDuplicateReportDto> FindCrossDuplicatesAsync(CancellationToken ct = default, bool forceRefresh = false);
    Task<RemoveDuplicatesResultDto> RemoveDuplicatesAsync(RemoveDuplicatesRequest req, CancellationToken ct = default);
    Task<MergePlaylistsResultDto> MergePlaylistsAsync(MergePlaylistsRequest req, CancellationToken ct = default);
    MergePreviewDto PreviewMerge(MergePreviewRequest req);
    List<PendingUploadDto> GetPendingUploads();
    Task<UploadResultDto> UploadPendingAsync(string id, CancellationToken ct = default);
    void DiscardPending(string id);
    PendingSongMoveDto? StageSongAssignment(AssignSongRequest req);
    List<string> GetSongLocations(string videoId);
    Dictionary<string, List<string>> GetSongLocationsBatch(List<string> videoIds);
    int StageRemoveFromPlaylist(string playlistId, List<string> videoIds);
    List<PendingSongMoveDto> GetPendingSongMoves();
    Task<SongMoveUploadResultDto> UploadSongMoveAsync(string id, CancellationToken ct = default);
    void DiscardSongMove(string id);
    Task<RefreshAllResultDto> RefreshAllAsync(CancellationToken ct = default);
    Task<List<PlaylistArchivedInfoDto>> GetArchivedPlaylistsAsync(CancellationToken ct = default);
}

