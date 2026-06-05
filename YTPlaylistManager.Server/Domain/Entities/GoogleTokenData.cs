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
}