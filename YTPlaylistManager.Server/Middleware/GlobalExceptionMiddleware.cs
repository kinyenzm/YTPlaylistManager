using System.Net;
using System.Text.Json;
using YTPlaylistManager.Server.Domain.Exceptions;

namespace YTPlaylistManager.Server.Middleware;

public sealed class GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (NotAuthenticatedException ex)
        {
            // Sesión Google ausente/expirada (defensiva; el caso normal lo corta el filtro RequireGoogleSession).
            logger.LogWarning(ex, "Sin sesión Google: {Message}", ex.Message);
            await WriteErrorResponse(context, HttpStatusCode.Unauthorized, ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Petición inválida (p. ej. NewPlaylistTitle requerido en merge).
            logger.LogWarning(ex, "Solicitud inválida: {Message}", ex.Message);
            await WriteErrorResponse(context, HttpStatusCode.BadRequest, ex.Message);
        }
        catch (Google.GoogleApiException ex)
        {
            // Error de la API de Google/YouTube (404 playlist inexistente, 403, cuota agotada, etc.).
            var status = ex.HttpStatusCode != 0 ? ex.HttpStatusCode : HttpStatusCode.BadGateway;
            logger.LogWarning(ex, "Error de la API de YouTube ({Status}): {Message}", status, ex.Message);
            await WriteErrorResponse(context, status, ex.Error?.Message ?? "Error de la API de YouTube.");
        }
        catch (AiUnavailableException ex)
        {
            // El frontend muestra "Revisa la configuración de IA" al recibir 503 del classify.
            logger.LogWarning(ex, "IA no disponible: {Message}", ex.Message);
            await WriteErrorResponse(context, HttpStatusCode.ServiceUnavailable, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Error al comunicarse con servicio externo");
            await WriteErrorResponse(context, HttpStatusCode.BadGateway,
                "Error al comunicarse con el servicio externo.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error no controlado: {Message}", ex.Message);
            await WriteErrorResponse(context, HttpStatusCode.InternalServerError,
                "Ocurrió un error interno en el servidor.");
        }
    }

    private static async Task WriteErrorResponse(HttpContext context, HttpStatusCode statusCode, string message)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var response = new { message, statusCode = (int)statusCode };
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
    }
}
