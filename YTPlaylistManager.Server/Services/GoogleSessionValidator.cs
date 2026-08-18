using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Puente entre el cliente de Google y <see cref="GoogleTokenStore"/>. Sin esto el
/// access token renovado vive solo en memoria y se pierde al terminar la petición:
/// el archivo se queda con el vencido y CADA llamada vuelve a renovar.
/// </summary>
public sealed class GoogleTokenDataStore(GoogleTokenStore store) : IDataStore
{
    public Task StoreAsync<T>(string key, T value)
    {
        if (value is TokenResponse tr && !string.IsNullOrEmpty(tr.AccessToken))
        {
            var cur = store.Load() ?? new GoogleTokenData();
            cur.AccessToken = tr.AccessToken;
            // Google no reemite refresh_token en cada renovación: se conserva el vigente.
            if (!string.IsNullOrEmpty(tr.RefreshToken)) cur.RefreshToken = tr.RefreshToken;
            if (!string.IsNullOrEmpty(tr.Scope)) cur.Scope = tr.Scope;
            var issued = tr.IssuedUtc == default ? DateTime.UtcNow : tr.IssuedUtc.ToUniversalTime();
            cur.ExpiresAtUtc = issued.AddSeconds(tr.ExpiresInSeconds ?? 3600);
            store.Save(cur);
        }
        return Task.CompletedTask;
    }

    // El token siempre se pasa explícitamente al construir la credencial: no se lee de aquí.
    public Task<T> GetAsync<T>(string key) => Task.FromResult<T>(default!);
    public Task DeleteAsync<T>(string key) => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
}

/// <summary>
/// Responde si la sesión de Google sirve DE VERDAD. Que exista un refresh token no
/// prueba nada: puede estar revocado o vencido, que es justo lo que significa
/// invalid_grant. Cuando el access token está vencido se comprueba contra Google
/// (el endpoint de token no gasta cuota de YouTube) y, si el refresh está muerto,
/// se invalida el token guardado para que la app deje de decir "conectado".
/// </summary>
public sealed class GoogleSessionValidator(
    GoogleTokenStore store,
    GoogleTokenDataStore dataStore,
    IConfiguration cfg,
    ILogger<GoogleSessionValidator> logger)
{
    public GoogleAuthorizationCodeFlow CreateFlow() =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = cfg["Google:ClientId"],
                ClientSecret = cfg["Google:ClientSecret"],
            },
            Scopes = cfg.GetSection("Google:Scopes").Get<string[]>() ?? [],
            DataStore = dataStore,
        });

    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        var t = store.Load();
        if (t is null || string.IsNullOrEmpty(t.AccessToken)) return false;

        // Access token vigente: nada que preguntar.
        if (t.ExpiresAtUtc > DateTime.UtcNow) return true;

        if (string.IsNullOrEmpty(t.RefreshToken))
        {
            Invalidate(t);
            return false;
        }

        try
        {
            var fresh = await CreateFlow().RefreshTokenAsync("me", t.RefreshToken, ct);
            // El DataStore ya persistió el token renovado.
            return !string.IsNullOrEmpty(fresh.AccessToken);
        }
        catch (TokenResponseException ex)
        {
            logger.LogWarning("Refresh token rechazado ({Error}); se cierra la sesión local.", ex.Error?.Error);
            Invalidate(t);
            return false;
        }
    }

    /// <summary>Marca la sesión como muerta conservando el refresh para migrar el UserKey legado.</summary>
    public void Invalidate(GoogleTokenData token)
    {
        if (string.IsNullOrEmpty(token.AccessToken)) return;
        token.AccessToken = "";
        token.ExpiresAtUtc = DateTime.MinValue;
        store.Save(token);
    }
}
