using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.Domain.Entities;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OperationsController(OperationLog log) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<OperationLogEntry>>(StatusCodes.Status200OK)]
    public IActionResult Listar() => Ok(log.LoadAll());
}
