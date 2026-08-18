using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Filters;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequireGoogleSession]
public sealed class PlaylistsController(IYouTubeService youtube, DuplicateService duplicates, IAiClassifier ai) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<PlaylistDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool refresh,
        [FromQuery] bool includeArchived,
        CancellationToken ct)
        => Ok(await youtube.GetMyPlaylistsAsync(ct, refresh, includeArchived));

    [HttpGet("{id}/items")]
    [ProducesResponseType<List<PlaylistItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Items(string id, [FromQuery] bool cacheOnly, CancellationToken ct)
        => cacheOnly
            ? Ok(youtube.GetCachedItems(id))                       // 0 cuota, nunca lee YouTube
            : Ok(await youtube.GetPlaylistItemsAsync(id, ct));

    [HttpGet("{id}/duplicates")]
    [ProducesResponseType<DuplicateReportDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Duplicates(string id, CancellationToken ct)
        => Ok(await duplicates.FindDuplicatesAsync(id, ct));

    [HttpGet("cross-duplicates")]
    [ProducesResponseType<CrossDuplicateReportDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CrossDuplicates([FromQuery] bool refresh, CancellationToken ct)
        => Ok(await duplicates.FindCrossDuplicatesAsync(ct, refresh));

    [HttpPost("remove-duplicates")]
    [ProducesResponseType<RemoveDuplicatesResultDto>(StatusCodes.Status200OK)]
    public IActionResult RemoveDuplicates([FromBody] RemoveDuplicatesRequest req)
        => Ok(duplicates.RemoveDuplicates(req));

    [HttpPost("merge")]
    [ProducesResponseType<MergePlaylistsResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Merge([FromBody] MergePlaylistsRequest req)
        => Ok(youtube.MergePlaylists(req));

    [HttpPost("merge/preview")]
    [ProducesResponseType<MergePreviewDto>(StatusCodes.Status200OK)]
    public IActionResult PreviewMerge([FromBody] MergePreviewRequest req)
        => Ok(youtube.PreviewMerge(req));

    [HttpGet("pending-uploads")]
    [ProducesResponseType<List<PendingUploadDto>>(StatusCodes.Status200OK)]
    public IActionResult PendingUploads()
        => Ok(youtube.GetPendingUploads());

    [HttpPost("pending-uploads/{id}/upload")]
    [ProducesResponseType<UploadResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadPending(string id, [FromQuery] int? limit, CancellationToken ct)
        => Ok(await youtube.UploadPendingAsync(id, limit, ct));

    [HttpDelete("pending-uploads/{id}")]
    public IActionResult DiscardPending(string id)
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
    public async Task<IActionResult> Classify(string id, [FromBody] ClassifyRequest req, CancellationToken ct)
    {
        var items = await youtube.GetPlaylistItemsAsync(id, ct);
        var groups = await ai.ClassifyAsync(items, req.Mode ?? "genre", ct);
        return Ok(new ClassifyResultDto(id, req.Mode ?? "genre", groups));
    }
}
