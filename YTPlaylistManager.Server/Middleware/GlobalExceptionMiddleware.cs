using System.Net;
using System.Text.Json;
using YTPlaylistManager.Server.Domain.Exceptions;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Middleware;

public sealed class GlobalExceptionMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionMiddleware> logger,
    QuotaTracker quota,
    GoogleTokenStore tokenStore)
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
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex)
        {
            // Token expirado o revocado: el refresh_token ya no es válido. Se invalida
            // el token guardado (AccessToken en blanco) para que las siguientes llamadas
            // fallen rápido en el filter y /auth/status refleje la desconexión, en vez
            // de quedar "conectado" fantasma sirviendo caché para siempre. El refresh
            // muerto se conserva solo como referencia para migrar el UserKey legacy.
            logger.LogWarning(ex, "Token de Google expirado/revocado: {Error}", ex.Error?.Error);
            var t = tokenStore.Load();
            if (t is not null && !string.IsNullOrEmpty(t.AccessToken))
            {
                t.AccessToken = "";
                t.ExpiresAtUtc = DateTime.MinValue;
                tokenStore.Save(t);
            }
            await WriteErrorResponse(context, HttpStatusCode.Unauthorized,
                "La sesión de Google expiró. Cierra sesión y vuelve a conectarte.");
        }
        catch (Google.GoogleApiException ex)
        {
            // Error de la API de Google/YouTube (404 playlist inexistente, 403, cuota agotada, etc.).
            // Si es cuota agotada, el contador local se fija al límite: el restante queda en 0
            // hasta el reinicio diario, sin esperar a que la app "gaste" hasta ahí.
            if (QuotaTracker.IsQuotaError(ex)) quota.MarkExhausted();
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // El cliente cerró la conexión antes de que el servidor terminara — normal, no es un error.
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 499;
        }
        catch (OperationCanceledException ex)
        {
            // Timeout interno u otra cancelación no iniciada por el cliente.
            logger.LogWarning(ex, "Operación cancelada");
            await WriteErrorResponse(context, HttpStatusCode.ServiceUnavailable,
                "La operación fue cancelada. Intenta de nuevo.");
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
