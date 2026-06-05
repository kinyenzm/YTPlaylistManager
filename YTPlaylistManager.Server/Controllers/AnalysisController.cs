using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AnalysisController : ControllerBase
{
    private readonly ISongSearchService _searchService;
    private readonly ILogger<AnalysisController> _logger;

    public AnalysisController(ISongSearchService searchService, ILogger<AnalysisController> logger)
    {
        _searchService = searchService;
        _logger = logger;
    }

    /// <summary>
    /// Pre-análisis de duplicados entre múltiples playlists (SIN tocar YouTube API).
    /// Útil para el paso 0 del MergeWizard: mostrar estimaciones antes de ejecutar.
    /// </summary>
    [HttpPost("duplicates")]
    public ActionResult<DuplicateAnalysisDto> AnalyzeDuplicates(
        [FromBody] List<string> playlistIds,
        [FromQuery] string? target = null)
    {
        try
        {
            if (playlistIds == null || playlistIds.Count == 0)
                return BadRequest("Debe proporcionar al menos una playlistId");

            if (playlistIds.Count > 10)
                return BadRequest("Máximo 10 playlists a analizar");

            var analysis = _searchService.AnalyzeDuplicatesForMerge(playlistIds, target);

            _logger.LogInformation(
                "Duplicate analysis completed: playlists={Count}, exactDups={Exact}, fuzzyDups={Fuzzy}, unique={Unique}, quota={Quota}, preview={Preview}",
                playlistIds.Count, analysis.TotalDuplicatesExact, analysis.TotalDuplicatesFuzzy,
                analysis.TotalUniqueItems, analysis.EstimatedQuotaCost, analysis.Preview?.Count ?? 0
            );

            return Ok(analysis);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in duplicate analysis");
            return StatusCode(500, new { message = "Error al analizar duplicados", error = ex.Message });
        }
    }
}
