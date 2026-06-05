using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Filters;

/// <summary>
/// Exige una sesión Google activa antes de ejecutar la acción. Si no hay token devuelve
/// 401 SIN lanzar excepción — así no se interrumpe el debugger ni se usa el throw como
/// control de flujo. El caso de token expirado a mitad de llamada lo cubre el middleware.
/// </summary>
public sealed class RequireGoogleSessionAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<GoogleTokenStore>();
        var token = store.Load();

        if (token is null || string.IsNullOrEmpty(token.AccessToken))
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
