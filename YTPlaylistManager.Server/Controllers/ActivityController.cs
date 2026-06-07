using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ActivityController(ActivityBroadcaster activity) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Stream SSE de la actividad real en YouTube (insertar/quitar/borrar) en vivo.</summary>
    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var reader = activity.Subscribe(out var ch);
        try
        {
            await Response.WriteAsync(": ok\n\n", ct);
            await Response.Body.FlushAsync(ct);

            // Reenviar lo reciente al conectar.
            foreach (var e in activity.Recent())
                await WriteEvent(e, ct);

            await foreach (var e in reader.ReadAllAsync(ct))
                await WriteEvent(e, ct);
        }
        catch (OperationCanceledException) { /* cliente cerró */ }
        finally { activity.Unsubscribe(ch); }
    }

    private async Task WriteEvent(ActivityEvent e, CancellationToken ct)
    {
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(e, Json)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
