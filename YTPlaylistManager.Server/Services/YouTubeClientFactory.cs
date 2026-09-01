using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using YTPlaylistManager.Server.Domain.Exceptions;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Construye el cliente autenticado de YouTube y resuelve la clave de cuenta.
/// Scoped: la clave se memoriza por request (cada cálculo relee el archivo del
/// token y hace un SHA256, y una petición la consulta decenas de veces).
/// </summary>
public sealed class YouTubeClientFactory(GoogleTokenStore tokenStore, GoogleSessionValidator session)
{
    private string? _userKey;

    /// <summary>Clave estable por cuenta. No gasta cuota.</summary>
    public string CurrentUserKey()
    {
        if (_userKey is not null) return _userKey;
        var t = tokenStore.Load();
        // Preferencia: AccountId (channel id, estable entre logins). Fallback legado:
        // refresh token — rota si Google emite uno nuevo y fragmenta los datos.
        return _userKey = UserKeys.FromSeed(
            !string.IsNullOrEmpty(t?.AccountId) ? t.AccountId : t?.RefreshToken ?? "anon");
    }

    private UserCredential BuildCredential()
    {
        var token = tokenStore.Load()
            ?? throw new NotAuthenticatedException("No hay sesión Google activa. Visita /api/auth/login primero.");

        // Flujo con DataStore: el access token renovado se persiste en vez de perderse
        // al terminar la petición.
        var flow = session.CreateFlow();

        var tokenResponse = new TokenResponse
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresInSeconds = (long)Math.Max(0, (token.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds),
            IssuedUtc = DateTime.UtcNow.AddSeconds(-1),
            Scope = token.Scope
        };

        return new UserCredential(flow, "me", tokenResponse);
    }

    public Google.Apis.YouTube.v3.YouTubeService BuildClient() =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = BuildCredential(),
            ApplicationName = "YTPlaylistManager"
        });

    /// <summary>Cliente de Drive con la misma credencial (respaldo en appDataFolder).</summary>
    public Google.Apis.Drive.v3.DriveService BuildDriveClient() =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = BuildCredential(),
            ApplicationName = "YTPlaylistManager"
        });
}
