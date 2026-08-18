using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Filters;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequireGoogleSession]
public sealed class SongsController(
    ISongSearchService searchService,
    SongMoveService songMoves,
    RecoveryService recovery) : ControllerBase
{

    /// <summary>
    /// Búsqueda bidireccional de canciones en caché.
    /// Soporta búsqueda por videoId (exacto + parcial) y por nombre (fuzzy + normalizado).
    /// </summary>
    [HttpPost("search")]
    public ActionResult<List<SongSearchResultDto>> Search([FromBody] SongSearchQueryDto query)
    {
        if (string.IsNullOrWhiteSpace(query.VideoIdPartial) && string.IsNullOrWhiteSpace(query.SongNameFuzzy))
            return BadRequest(new { message = "Debe proporcionar videoIdPartial o songNameFuzzy" });

        return Ok(searchService.SearchCombined(query.VideoIdPartial, query.SongNameFuzzy, query.SearchScope));
    }

    /// <summary>Por playlist: cuántas canciones (videos disponibles) están también en otra lista. 100% caché, 0 cuota.</summary>
    [HttpGet("duplicate-counts")]
    [ProducesResponseType<Dictionary<string, int>>(StatusCodes.Status200OK)]
    public IActionResult DuplicateCounts()
        => Ok(searchService.GetDuplicateCountsByPlaylist());

    // ── Asignar una canción a varias/una playlist (staged: local → pendiente → subir) ──

    /// <summary>Aplica en local la reasignación de una canción y la deja pendiente de subir.</summary>
    [HttpPost("assign")]
    [ProducesResponseType<PendingSongMoveDto>(StatusCodes.Status200OK)]
    public IActionResult Assign([FromBody] AssignSongRequest req)
        => Ok(songMoves.StageSongAssignment(req));

    /// <summary>Playlists (ids) donde está la canción ahora (caché, 0 cuota).</summary>
    [HttpGet("{videoId}/locations")]
    [ProducesResponseType<List<string>>(StatusCodes.Status200OK)]
    public IActionResult Locations(string videoId)
        => Ok(songMoves.GetSongLocations(videoId));

    /// <summary>Ubicaciones de varias canciones a la vez (videoId → ids de listas).</summary>
    [HttpPost("locations")]
    public IActionResult LocationsBatch([FromBody] List<string> videoIds)
        => Ok(songMoves.GetSongLocationsBatch(videoIds));

    /// <summary>Encola quitar copias específicas (por playlistItemId) de una playlist (staged).</summary>
    [HttpPost("remove-items")]
    public IActionResult RemoveItems([FromBody] RemoveItemsRequest req)
        => Ok(new StagedCountDto(songMoves.StageRemoveItemsFromPlaylist(req.PlaylistId, req.PlaylistItemIds)));

    /// <summary>
    /// Canciones huérfanas: conocidas por la app pero fuera de todas las playlists
    /// actuales (listas borradas, remociones). 0 cuota — solo caché y actividad.
    /// </summary>
    [HttpGet("recoverable")]
    [ProducesResponseType<List<RecoverableSongDto>>(StatusCodes.Status200OK)]
    public IActionResult Recoverable()
        => Ok(recovery.GetRecoverableSongs());

    /// <summary>
    /// Encola la recuperación (lista existente o nueva) como pendiente de subida
    /// reanudable; la subida real se hace desde el panel de pendientes.
    /// </summary>
    [HttpPost("recover")]
    [ProducesResponseType<PendingUploadDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recover([FromBody] RecoverSongsRequest req, CancellationToken ct)
        => Ok(await recovery.StageRecoveryAsync(req, ct));

    [HttpGet("pending-moves")]
    [ProducesResponseType<List<PendingSongMoveDto>>(StatusCodes.Status200OK)]
    public IActionResult PendingMoves()
        => Ok(songMoves.GetPendingSongMoves());

    [HttpPost("pending-moves/{id}/upload")]
    [ProducesResponseType<SongMoveUploadResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadMove(string id, CancellationToken ct)
        => Ok(await songMoves.UploadSongMoveAsync(id, ct));

    [HttpPost("pending-moves/upload-all")]
    [ProducesResponseType<SongMoveBulkResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadAllMoves(CancellationToken ct)
        => Ok(await songMoves.UploadAllSongMovesAsync(ct));

    [HttpDelete("pending-moves/{id}")]
    public IActionResult DiscardMove(string id)
    {
        songMoves.DiscardSongMove(id);
        return NoContent();
    }

    [HttpDelete("pending-moves")]
    public IActionResult DiscardAllMoves()
    {
        songMoves.DiscardAllSongMoves();
        return NoContent();
    }
}
