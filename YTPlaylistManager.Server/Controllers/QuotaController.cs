using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class QuotaController(QuotaTracker quota) : ControllerBase
{
    /// <summary>Cuota estimada usada hoy (se reinicia diario, hora Pacífico).</summary>
    [HttpGet]
    [ProducesResponseType<QuotaDto>(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        var (used, limit, date) = quota.Get();
        return Ok(new QuotaDto(used, limit, Math.Max(0, limit - used), date));
    }
}
