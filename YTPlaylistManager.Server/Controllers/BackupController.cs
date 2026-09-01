using System.Text;
using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.DTOs;
using YTPlaylistManager.Server.Filters;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

/// <summary>
/// Respaldo del registro local: exportar/importar un .json y subir/restaurar
/// contra Google Drive. Los errores de Drive NO llegan al middleware global:
/// un 403 de Drive no es "cuota de YouTube agotada" y no debe tocar el marcador.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[RequireGoogleSession]
public sealed class BackupController(
    BackupService backup,
    YouTubeClientFactory clientFactory,
    ILogger<BackupController> logger) : ControllerBase
{
    [HttpGet("export")]
    public IActionResult Export()
    {
        var json = backup.ExportJson();
        var name = $"ytpm-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        return File(Encoding.UTF8.GetBytes(json), "application/json", name);
    }

    [HttpPost("import")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 64 * 1024 * 1024)]
    [ProducesResponseType<BackupImportResultDto>(StatusCodes.Status200OK)]
    public IActionResult Import(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new BackupErrorDto("empty_file", "No llegó ningún archivo."));

        using var stream = file.OpenReadStream();
        // Las validaciones del contenido lanzan ArgumentException → 400 del middleware.
        return Ok(backup.Import(stream, clientFactory.CurrentUserKey()));
    }

    [HttpGet("status")]
    [ProducesResponseType<BackupStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct)
        => Ok(await backup.StatusAsync(ct));

    [HttpPost("drive/upload")]
    public async Task<IActionResult> DriveUpload(CancellationToken ct)
    {
        try
        {
            await backup.UploadToDriveAsync(ct);
            return Ok(new AckDto(true));
        }
        catch (Google.GoogleApiException ex)
        {
            return MapDriveError(ex);
        }
    }

    [HttpPost("drive/restore")]
    [ProducesResponseType<BackupImportResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DriveRestore(CancellationToken ct)
    {
        try
        {
            return Ok(await backup.RestoreFromDriveAsync(clientFactory.CurrentUserKey(), ct));
        }
        catch (Google.GoogleApiException ex)
        {
            return MapDriveError(ex);
        }
    }

    private ObjectResult MapDriveError(Google.GoogleApiException ex)
    {
        var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason ?? "";

        // Falta el scope drive.appdata en el grant vigente → reconectar con consent.
        if (reason is "insufficientPermissions" or "insufficientScopes"
            || ex.Message.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status409Conflict, new BackupErrorDto(
                "drive_scope_missing",
                "Falta el permiso de Drive. Reconecta con Google para habilitar el respaldo."));

        // La API de Drive no está habilitada en el proyecto de Google Cloud (paso manual).
        if (reason == "accessNotConfigured"
            || ex.Message.Contains("has not been used", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new BackupErrorDto(
                "drive_api_disabled",
                "La API de Google Drive no está habilitada en el proyecto de Google Cloud."));

        logger.LogWarning(ex, "Error de Google Drive ({Reason}).", reason);
        return StatusCode(StatusCodes.Status502BadGateway, new BackupErrorDto(
            "drive_error", "No se pudo hablar con Google Drive. Intenta de nuevo."));
    }
}
