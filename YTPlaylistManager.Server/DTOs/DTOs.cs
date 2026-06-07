namespace YTPlaylistManager.Server.DTOs;

// ── Playlist DTOs ──

public record PlaylistDto(
    string Id,
    string Title,
    string? Description,
    int ItemCount,
    string? ThumbnailUrl,
    string? Privacy = null,           // "public" | "private" | "unlisted"
    bool IsArchived = false,          // local-only: marcada como consolidada
    string? ArchivedIntoPlaylistId = null,
    string? ArchivedIntoPlaylistTitle = null,
    bool QueuedForMerge = false,      // en cola: sus canciones ya se unieron (local); se borrará al subir
    string? QueuedIntoTitle = null
);

public record PlaylistItemDto(
    string PlaylistItemId,    // Id del item dentro de la playlist (necesario para borrar/reordenar)
    string VideoId,
    string Title,
    string? ChannelTitle,
    int Position,
    string? ThumbnailUrl
);

// ── Duplicados ──

public record DuplicateGroupDto(
    string Key,               // videoId o título normalizado
    string MatchType,         // "videoId" | "normalizedTitle"
    List<PlaylistItemDto> Items
);

public record DuplicateReportDto(
    string PlaylistId,
    string PlaylistTitle,
    int TotalItems,
    int DuplicateCount,
    List<DuplicateGroupDto> Groups
);

public record RemoveDuplicatesRequest(
    string PlaylistId,
    string Strategy = "videoId"       // "videoId" | "normalizedTitle"
);

public record RemoveDuplicatesResultDto(
    string PlaylistId,
    int Removed,
    int Kept
);

// ── Duplicados entre playlists (mismo video en 2+ playlists) ──

public record CrossPlaylistRefDto(
    string PlaylistId,
    string PlaylistTitle
);

public record CrossDuplicateDto(
    string VideoId,
    string Title,
    int PlaylistCount,
    List<CrossPlaylistRefDto> Playlists
);

public record CrossDuplicateReportDto(
    int TotalPlaylists,
    int TotalGroups,
    List<CrossDuplicateDto> Groups,
    int Scanned = 0,                 // playlists leídas con éxito
    int Failed = 0                   // playlists que no se pudieron leer (cuota/privadas/borradas)
);

// ── Merge ──

public record MergePlaylistsRequest(
    List<string> SourcePlaylistIds,
    string? TargetPlaylistId,         // si es null, se crea una nueva
    string? NewPlaylistTitle,         // requerido cuando TargetPlaylistId es null
    bool DeduplicateOnMerge = true,
    string Privacy = "private",
    bool DeleteSources = false        // archivar las playlists originales tras aplicar (local)
);

public record MergePlaylistsResultDto(
    string TargetPlaylistId,
    string TargetPlaylistTitle,
    int Added,
    int SkippedDuplicates,
    int ArchivedSources = 0,
    string? ReviewId = null,          // id de la entrada en la cola de revisión
    int Failed = 0,
    bool Paused = false               // true si se cortó por límite diario de YouTube
);

public record MergePreviewRequest(string TargetPlaylistId, List<string> SourcePlaylistIds);

public record MergePreviewSongDto(
    string VideoId,
    string Title,
    string ChannelTitle,
    string? ThumbnailUrl,
    List<string> FromPlaylists       // títulos de las listas origen donde aparece (sin repetir filas)
);

public record MergePreviewDto(
    string TargetPlaylistId,
    string TargetPlaylistTitle,
    int ToAddCount,
    int AlreadyPresentCount,
    int EstimatedQuotaUnits,
    List<MergePreviewSongDto> ToAdd,
    List<string> Warnings
);

public record PendingUploadItemDto(
    string VideoId,
    string Title,
    string ChannelTitle,
    string? ThumbnailUrl,
    List<string> FromPlaylists
);

public record PendingUploadDto(
    string Id,
    string TargetPlaylistId,
    string TargetPlaylistTitle,
    int ItemCount,
    int EstimatedQuotaUnits,
    DateTime CreatedAtUtc,
    List<PendingUploadItemDto> Items,
    List<string> SourceTitles    // listas origen que se borrarán de YouTube al subir
);

public record UploadResultDto(
    string Id,
    string TargetPlaylistId,
    string TargetPlaylistTitle,
    int Uploaded,
    int Failed,
    bool Paused,             // se cortó por límite diario de YouTube
    int RemainingPending,    // canciones que quedaron sin subir (si Paused)
    int DeletedSources = 0,  // listas origen borradas de YouTube
    int RemainingSources = 0 // listas origen que faltan borrar (si se cortó)
);

// ── Asignar una canción a varias/una playlist (staged) ──

public record AssignSongRequest(
    string VideoId,
    string Title,
    string? ChannelTitle,
    string? ThumbnailUrl,
    List<string> DesiredPlaylistIds   // playlists donde debe quedar la canción
);

public record PendingSongMoveDto(
    string Id,
    string VideoId,
    string Title,
    string? ThumbnailUrl,
    List<string> AddTo,           // títulos de listas donde se va a agregar
    List<string> RemoveFrom,      // títulos de listas de donde se va a quitar
    int EstimatedQuotaUnits,
    DateTime CreatedAtUtc
);

public record SongMoveUploadResultDto(
    string Id,
    string VideoId,
    int Added,
    int Removed,
    int Failed,
    bool Paused,
    int RemainingOps
);

public record SongMoveBulkResultDto(
    int Moves,            // cuántos cambios había en la cola
    int Completed,        // cuántos se subieron del todo
    int Added,
    int Removed,
    int Failed,
    bool Paused,          // se cortó por límite diario
    int RemainingMoves    // cuántos quedan en la cola
);

public record RemoveFromPlaylistRequest(string PlaylistId, List<string> VideoIds);

public record QuotaDto(int Used, int Limit, int Remaining, string Date);

public record MergeReviewSummaryDto(
    string Id,
    string TargetPlaylistId,
    string TargetPlaylistTitle,
    List<MergeReviewSourceDto> Sources,
    int NewSongsCount,
    int DuplicateSongsCount,
    bool DeleteSources,
    DateTime CreatedAtUtc
);

public record MergeReviewSourceDto(
    string PlaylistId,
    string Title,
    int ItemCount
);

public record ApplyMergeReviewResultDto(
    string TargetPlaylistId,
    string TargetPlaylistTitle,
    int AddedItems,
    int ArchivedSources
);

public record PendingMergeDto(
    string Id,
    string TargetPlaylistId,
    string TargetPlaylistTitle,
    int PendingCount,
    DateTime CreatedAtUtc
);

// ── Clasificación IA ──

public record ClassifyRequest(
    string PlaylistId,
    string Mode = "genre"             // "genre" | "mood" | "decade"
);

public record ClassifiedSongDto(
    string VideoId,
    string Title,
    string Group
);

public record ClassifyResultDto(
    string PlaylistId,
    string Mode,
    Dictionary<string, List<ClassifiedSongDto>> Groups
);

// ── Búsqueda bidireccional ──

public record SongSearchQueryDto(
    string? VideoIdPartial,       // null, exacto, o parcial
    string? SongNameFuzzy,        // fuzzy search en titleNormalized
    string SearchScope            // "all", "active", "archived"
);

public record SongSearchResultDto(
    string VideoId,
    string Title,
    string ChannelTitle,
    string OriginalPlaylistId,
    string OriginalPlaylistTitle,
    int OriginalPosition,
    string? CurrentPlaylistId,
    string? CurrentPlaylistTitle,
    int? CurrentPosition,
    int AppearsInCount,
    List<string> AppearsInPlaylistIds,
    bool IsDuplicate,
    bool WasMerged,
    string? MergeId
);

// ── Auditoría y trazabilidad ──

public record SongMovementEventDto(
    DateTime Date,
    string EventType,             // "cached", "merged", "deleted", "updated"
    string PlaylistId,
    string PlaylistTitle,
    int Position,
    string? SourcePlaylistId,
    string? Reason
);

public record SongMovementLogDto(
    string VideoId,
    string SongTitle,
    List<SongMovementEventDto> Events
);

public record PlaylistArchivedInfoDto(
    string Id,
    string Title,
    DateTime ArchivedAt,
    string MergedIntoPlaylistId,
    string MergedIntoPlaylistTitle,
    int SongsCount
);

public record RefreshAllResultDto(
    int PlaylistsRefreshed,
    int ItemsRefreshed,
    int PlaylistsSkipped,
    int QuotaUsed
);

public record CacheStatusDto(
    int PlaylistsCount,
    int TotalSongs,
    DateTime LastUpdated,
    int MergesCount,
    int ArchivedPlaylistsCount
);

