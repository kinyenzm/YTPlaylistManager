using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Filters;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SongsController : ControllerBase
{
    private readonly ISongSearchService _searchService;
    private readonly IYouTubeService _youtube;
    private readonly ILogger<SongsController> _logger;

    public SongsController(ISongSearchService searchService, IYouTubeService youtube, ILogger<SongsController> logger)
    {
        _searchService = searchService;
        _youtube = youtube;
        _logger = logger;
    }

    /// <summary>
    /// Búsqueda bidireccional de canciones en caché.
    /// Soporta búsqueda por videoId (exacto + parcial) y por nombre (fuzzy + normalizado).
    /// </summary>
    [HttpPost("search")]
    public ActionResult<List<SongSearchResultDto>> Search([FromBody] SongSearchQueryDto query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query.VideoIdPartial) && string.IsNullOrWhiteSpace(query.SongNameFuzzy))
                return BadRequest(new { message = "Debe proporcionar videoIdPartial o songNameFuzzy" });

            var results = _searchService.SearchCombined(
                query.VideoIdPartial,
                query.SongNameFuzzy,
                query.SearchScope
            );

            _logger.LogInformation(
                "Search completed: videoId={VideoId}, name={Name}, scope={Scope}, results={Count}",
                query.VideoIdPartial, query.SongNameFuzzy, query.SearchScope, results.Count
            );

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in search endpoint");
            return StatusCode(500, new { message = "Error al buscar canciones", error = ex.Message });
        }
    }

    // ── Asignar una canción a varias/una playlist (staged: local → pendiente → subir) ──

    /// <summary>Aplica en local la reasignación de una canción y la deja pendiente de subir.</summary>
    [HttpPost("assign")]
    [RequireGoogleSession]
    [ProducesResponseType<PendingSongMoveDto>(StatusCodes.Status200OK)]
    public IActionResult Assign([FromBody] AssignSongRequest req)
        => Ok(_youtube.StageSongAssignment(req));

    /// <summary>Playlists (ids) donde está la canción ahora (caché, 0 cuota).</summary>
    [HttpGet("{videoId}/locations")]
    [RequireGoogleSession]
    [ProducesResponseType<List<string>>(StatusCodes.Status200OK)]
    public IActionResult Locations(string videoId)
        => Ok(_youtube.GetSongLocations(videoId));

    /// <summary>Ubicaciones de varias canciones a la vez (videoId → ids de listas).</summary>
    [HttpPost("locations")]
    [RequireGoogleSession]
    public IActionResult LocationsBatch([FromBody] List<string> videoIds)
        => Ok(_youtube.GetSongLocationsBatch(videoIds));

    /// <summary>Encola quitar varias canciones de una playlist (staged).</summary>
    [HttpPost("remove-from-playlist")]
    [RequireGoogleSession]
    public IActionResult RemoveFromPlaylist([FromBody] RemoveFromPlaylistRequest req)
        => Ok(new { staged = _youtube.StageRemoveFromPlaylist(req.PlaylistId, req.VideoIds) });

    [HttpGet("pending-moves")]
    [RequireGoogleSession]
    [ProducesResponseType<List<PendingSongMoveDto>>(StatusCodes.Status200OK)]
    public IActionResult PendingMoves()
        => Ok(_youtube.GetPendingSongMoves());

    [HttpPost("pending-moves/{id}/upload")]
    [RequireGoogleSession]
    [ProducesResponseType<SongMoveUploadResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadMove(string id, CancellationToken ct)
        => Ok(await _youtube.UploadSongMoveAsync(id, ct));

    [HttpPost("pending-moves/upload-all")]
    [RequireGoogleSession]
    [ProducesResponseType<SongMoveBulkResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadAllMoves(CancellationToken ct)
        => Ok(await _youtube.UploadAllSongMovesAsync(ct));

    [HttpDelete("pending-moves/{id}")]
    [RequireGoogleSession]
    public IActionResult DiscardMove(string id)
    {
        _youtube.DiscardSongMove(id);
        return NoContent();
    }

    [HttpDelete("pending-moves")]
    [RequireGoogleSession]
    public IActionResult DiscardAllMoves()
    {
        _youtube.DiscardAllSongMoves();
        return NoContent();
    }
}
