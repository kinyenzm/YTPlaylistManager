using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ActivityController(ActivityBroadcaster activity) : ControllerBase
{
    /// <summary>Historial persistido de actividad real en YouTube, más reciente primero.</summary>
    [HttpGet("log")]
    public ActionResult<List<ActivityEvent>> Log([FromQuery] int take = 200)
        => Ok(activity.History(take));
}
