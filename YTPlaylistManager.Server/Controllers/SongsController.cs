using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SongsController : ControllerBase
{
    private readonly ISongSearchService _searchService;
    private readonly ILogger<SongsController> _logger;

    public SongsController(ISongSearchService searchService, ILogger<SongsController> logger)
    {
        _searchService = searchService;
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
}
