using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Filters;

/// <summary>
/// Exige una sesión Google activa antes de ejecutar la acción. Si no la hay devuelve
/// 401 SIN lanzar excepción — así no se interrumpe el debugger ni se usa el throw como
/// control de flujo. "Activa" significa que el token sirve de verdad, no que exista un
/// refresh token: uno revocado devuelve invalid_grant. Así los endpoints que responden
/// solo con caché también cortan, en vez de aparentar sesión viva para siempre.
/// </summary>
public sealed class RequireGoogleSessionAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var validator = context.HttpContext.RequestServices.GetRequiredService<GoogleSessionValidator>();

        // Comprueba contra Google solo si el access token venció; si el refresh está
        // revocado el token queda invalidado y las siguientes llamadas cortan sin red.
        if (!await validator.IsAliveAsync(context.HttpContext.RequestAborted))
        {
            context.Result = new ObjectResult(new
            {
                message = "No hay sesión Google activa. Visita /api/auth/login primero.",
                statusCode = StatusCodes.Status401Unauthorized
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        await next();
    }
}
