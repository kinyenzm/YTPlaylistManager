namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Derivación única del UserKey. Preferencia: AccountId (channel id de YouTube,
/// estable entre logins); fallback legado: refresh token (cambia si Google emite
/// uno nuevo y fragmenta los datos — el motivo de la migración).
/// </summary>
public static class UserKeys
{
    /// <summary>Clave para el token actual (o "anon" sin sesion).</summary>
    public static string FromToken(Domain.Entities.GoogleTokenData? t) =>
        FromSeed(!string.IsNullOrEmpty(t?.AccountId) ? t.AccountId : t?.RefreshToken ?? "anon");

    public static string FromSeed(string seed)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes)[..16];
    }
}
