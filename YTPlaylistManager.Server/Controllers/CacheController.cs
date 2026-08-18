using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Filters;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

// Sirve datos cacheados, pero la regla de la app es "sin sesión no se muestra ni
// caché": mismo guard que el resto. Los errores los uniforma el middleware global
// (los try/catch por acción devolvían 500 con ex.Message y otra forma de JSON).
[ApiController]
[Route("api/[controller]")]
[RequireGoogleSession]
public sealed class CacheController(
    ISongSearchService searchService,
    PlaylistCacheStore cacheStore,
    ArchivedPlaylistsStore archivedStore,
    MergeReviewStore reviewStore,
    OperationLog log,
    IYouTubeService youtube) : ControllerBase
{
    /// <summary>Estado actual del caché (estadísticas globales)</summary>
    [HttpGet("status")]
    public ActionResult<CacheStatusDto> GetStatus()
    {
        var cacheData = cacheStore.Load();

        return Ok(new CacheStatusDto(
            cacheData?.Playlists?.Count ?? 0,
            cacheData?.Playlists?.Sum(p => p.ItemCount) ?? 0,
            cacheData?.CachedAtUtc ?? DateTime.UtcNow,
            log.LoadAll().Count(e => e.Operation.StartsWith("Merge")),
            archivedStore.LoadAll().Count));
    }

    /// <summary>Obtener historial completo de una canción</summary>
    [HttpGet("song/{videoId}/history")]
    public ActionResult<SongMovementLogDto> GetSongHistory(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return BadRequest(new { message = "videoId es requerido" });

        var timeline = searchService.GetSongTimeline(videoId);
        if (timeline is null)
            return NotFound(new { message = "Canción no encontrada" });

        return Ok(timeline);
    }

    /// <summary>Obtener lista de playlists archivadas (consolidadas en otra localmente)</summary>
    [HttpGet("playlists-archived")]
    public async Task<ActionResult<List<PlaylistArchivedInfoDto>>> GetArchivedPlaylists()
        => Ok(await youtube.GetArchivedPlaylistsAsync());

    /// <summary>Historial de merges aplicados localmente (cola de revisión).</summary>
    [HttpGet("merge-reviews")]
    public ActionResult<List<MergeReviewSummaryDto>> GetMergeReviews()
        => Ok(reviewStore.LoadAll()
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
            .ToList());
}
