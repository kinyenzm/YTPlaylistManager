using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CacheController : ControllerBase
{
    private readonly ISongSearchService _searchService;
    private readonly PlaylistCacheStore _cacheStore;
    private readonly ArchivedPlaylistsStore _archivedStore;
    private readonly MergeReviewStore _reviewStore;
    private readonly OperationLog _log;
    private readonly IYouTubeService _youtube;
    private readonly ILogger<CacheController> _logger;

    public CacheController(
        ISongSearchService searchService,
        PlaylistCacheStore cacheStore,
        ArchivedPlaylistsStore archivedStore,
        MergeReviewStore reviewStore,
        OperationLog log,
        IYouTubeService youtube,
        ILogger<CacheController> logger)
    {
        _searchService = searchService;
        _cacheStore = cacheStore;
        _archivedStore = archivedStore;
        _reviewStore = reviewStore;
        _log = log;
        _youtube = youtube;
        _logger = logger;
    }

    /// <summary>Estado actual del caché (estadísticas globales)</summary>
    [HttpGet("status")]
    public ActionResult<CacheStatusDto> GetStatus()
    {
        try
        {
            var cacheData = _cacheStore.Load();
            var archived = _archivedStore.LoadAll();

            int totalSongs = cacheData?.Playlists?.Sum(p => p.ItemCount) ?? 0;
            int playlistCount = cacheData?.Playlists?.Count ?? 0;
            int mergeCount = _log.LoadAll().Count(e => e.Operation.StartsWith("Merge"));

            var status = new CacheStatusDto(
                playlistCount,
                totalSongs,
                cacheData?.CachedAtUtc ?? DateTime.UtcNow,
                mergeCount,
                archived.Count
            );

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cache status");
            return StatusCode(500, new { message = "Error al obtener estado del caché", error = ex.Message });
        }
    }

    /// <summary>Obtener historial completo de una canción</summary>
    [HttpGet("song/{videoId}/history")]
    public ActionResult<SongMovementLogDto> GetSongHistory(string videoId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return BadRequest(new { message = "videoId es requerido" });

            var timeline = _searchService.GetSongTimeline(videoId);
            if (timeline == null)
                return NotFound(new { message = "Canción no encontrada" });

            return Ok(timeline);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting song history for {VideoId}", videoId);
            return StatusCode(500, new { message = "Error al obtener historial", error = ex.Message });
        }
    }

    /// <summary>Obtener lista de playlists archivadas (consolidadas en otra localmente)</summary>
    [HttpGet("playlists-archived")]
    public async Task<ActionResult<List<PlaylistArchivedInfoDto>>> GetArchivedPlaylists()
    {
        try
        {
            var archived = await _youtube.GetArchivedPlaylistsAsync();
            return Ok(archived);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting archived playlists");
            return StatusCode(500, new { message = "Error al obtener playlists archived", error = ex.Message });
        }
    }

    /// <summary>Historial de merges aplicados localmente (cola de revisión).</summary>
    [HttpGet("merge-reviews")]
    public ActionResult<List<MergeReviewSummaryDto>> GetMergeReviews()
    {
        try
        {
            var plans = _reviewStore.LoadAll()
                .OrderByDescending(p => p.CreatedAtUtc)
                .Select(p => new MergeReviewSummaryDto(
                    p.Id,
                    p.TargetPlaylistId,
                    p.TargetPlaylistTitle,
                    p.Sources.Select(s => new MergeReviewSourceDto(s.PlaylistId, s.Title, s.ItemCount)).ToList(),
                    p.NewSongs.Count,
                    p.DuplicateSongs.Count,
                    p.DeleteSources,
                    p.CreatedAtUtc))
                .ToList();
            return Ok(plans);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting merge reviews");
            return StatusCode(500, new { message = "Error al obtener historial de merges", error = ex.Message });
        }
    }
}
