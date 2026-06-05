using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Filters;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequireGoogleSession]
public sealed class PlaylistsController(IYouTubeService youtube, IAiClassifier ai) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<PlaylistDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(
        [FromQuery] bool refresh,
        [FromQuery] bool includeArchived,
        CancellationToken ct)
        => Ok(await youtube.GetMyPlaylistsAsync(ct, refresh, includeArchived));

    [HttpGet("{id}/items")]
    [ProducesResponseType<List<PlaylistItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Items(string id, CancellationToken ct)
        => Ok(await youtube.GetPlaylistItemsAsync(id, ct));

    [HttpGet("{id}/duplicates")]
    [ProducesResponseType<DuplicateReportDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Duplicados(string id, CancellationToken ct)
        => Ok(await youtube.FindDuplicatesAsync(id, ct));

    [HttpGet("cross-duplicates")]
    [ProducesResponseType<CrossDuplicateReportDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DuplicadosEntrePlaylists([FromQuery] bool refresh, CancellationToken ct)
        => Ok(await youtube.FindCrossDuplicatesAsync(ct, refresh));

    [HttpPost("remove-duplicates")]
    [ProducesResponseType<RemoveDuplicatesResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoverDuplicados([FromBody] RemoveDuplicatesRequest req, CancellationToken ct)
        => Ok(await youtube.RemoveDuplicatesAsync(req, ct));

    [HttpPost("merge")]
    [ProducesResponseType<MergePlaylistsResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Unir([FromBody] MergePlaylistsRequest req, CancellationToken ct)
        => Ok(await youtube.MergePlaylistsAsync(req, ct));

    [HttpPost("merge/preview")]
    [ProducesResponseType<MergePreviewDto>(StatusCodes.Status200OK)]
    public IActionResult PreviewUnir([FromBody] MergePreviewRequest req)
        => Ok(youtube.PreviewMerge(req));

    [HttpGet("pending-uploads")]
    [ProducesResponseType<List<PendingUploadDto>>(StatusCodes.Status200OK)]
    public IActionResult PendingUploads()
        => Ok(youtube.GetPendingUploads());

    [HttpPost("pending-uploads/{id}/upload")]
    [ProducesResponseType<UploadResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubirPendiente(string id, CancellationToken ct)
        => Ok(await youtube.UploadPendingAsync(id, ct));

    [HttpDelete("pending-uploads/{id}")]
    public IActionResult DescartarPendiente(string id)
    {
        youtube.DiscardPending(id);
        return NoContent();
    }

    [HttpPost("refresh-all")]
    [ProducesResponseType<RefreshAllResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshAll(CancellationToken ct)
        => Ok(await youtube.RefreshAllAsync(ct));

    [HttpPost("{id}/classify")]
    [ProducesResponseType<ClassifyResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Clasificar(string id, [FromBody] ClassifyRequest req, CancellationToken ct)
    {
        var items = await youtube.GetPlaylistItemsAsync(id, ct);
        var groups = await ai.ClassifyAsync(items, req.Mode ?? "genre", ct);
        return Ok(new ClassifyResultDto(id, req.Mode ?? "genre", groups));
    }
}
