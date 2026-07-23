namespace YTPlaylistManager.Server.Domain.Entities;

/// <summary>
/// Token OAuth del usuario, persistido localmente por <c>GoogleTokenStore</c>.
/// </summary>
public class GoogleTokenData
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string Scope { get; set; } = "";
    /// <summary>Id estable de la cuenta (channel id de YouTube); base del UserKey.</summary>
    public string? AccountId { get; set; }

    /// <summary>
    /// Criterio único de sesión válida (filter y /auth/status deben coincidir):
    /// access token presente y, si ya venció, un refresh token con el que el cliente
    /// de Google pueda renovarlo. Vencido y sin refresh = sesión muerta.
    /// </summary>
    public bool HasUsableSession =>
        !string.IsNullOrEmpty(AccessToken)
        && (ExpiresAtUtc > DateTime.UtcNow || !string.IsNullOrEmpty(RefreshToken));
}