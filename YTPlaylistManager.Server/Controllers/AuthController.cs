using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using YTPlaylistManager.Server.Domain.Entities;
using YTPlaylistManager.Server.Services;

namespace YTPlaylistManager.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController(
    IConfiguration cfg,
    IHttpClientFactory httpFactory,
    GoogleTokenStore store,
    QuotaTracker quota,
    PlaylistItemsCacheStore itemsCache,
    PlaylistCacheStore playlistCache,
    PendingUploadStore pendingUploads,
    PendingSongMoveStore songMoves) : ControllerBase
{
    // Inicia el flujo OAuth2 redirigiendo al consent screen de Google.
    [HttpGet("login")]
    public IActionResult Login()
    {
        var clientId = cfg["Google:ClientId"];
        var redirect = cfg["Google:RedirectUri"];
        var scopes = string.Join(" ", cfg.GetSection("Google:Scopes").Get<string[]>() ?? Array.Empty<string>());

        var url = "https://accounts.google.com/o/oauth2/v2/auth"
            + $"?client_id={Uri.EscapeDataString(clientId!)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirect!)}"
            + "&response_type=code"
            + $"&scope={Uri.EscapeDataString(scopes)}"
            + "&access_type=offline"
            + "&prompt=select_account";

        return Redirect(url);
    }

    // Callback OAuth2: intercambia el code por tokens y los guarda.
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code)) return BadRequest("Falta code");

        var http = httpFactory.CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = cfg["Google:ClientId"]!,
            ["client_secret"] = cfg["Google:ClientSecret"]!,
            ["redirect_uri"] = cfg["Google:RedirectUri"]!,
            ["grant_type"] = "authorization_code"
        });

        var resp = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return Problem($"OAuth error: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var token = new GoogleTokenData
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()),
            Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : ""
        };

        // Si no vino refresh_token (re-consent), conservar el anterior si existe.
        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            var prev = store.Load();
            token.RefreshToken = prev?.RefreshToken;
            token.AccountId = prev?.AccountId;
        }

        store.Save(token);

        // AccountId (channel id): base estable del UserKey entre logins. Si falla,
        // queda null y se usa el fallback legado (hash del refresh token) — el login
        // no se rompe por esto.
        await ResolveAccountIdAndMigrate(http, token, ct);

        var ui = cfg["Cors:AllowedOrigins:0"] ?? "http://localhost:4200";
        return Redirect($"{ui}/?auth=ok");
    }

    private async Task ResolveAccountIdAndMigrate(HttpClient http, GoogleTokenData token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/youtube/v3/channels?part=id&mine=true");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
            var resp = await http.SendAsync(req, ct);
            quota.Add(1);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var channelId = doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0
                ? items[0].GetProperty("id").GetString()
                : null;
            if (string.IsNullOrEmpty(channelId)) return;

            token.AccountId = channelId;
            store.Save(token);

            // Migración one-shot: la app es single-user (un solo token file), así que
            // toda clave preexistente en los stores pertenece a esta misma cuenta.
            var newKey = UserKeys.FromSeed(channelId);
            itemsCache.MigrateToKey(newKey);
            playlistCache.MigrateToKey(newKey);
            pendingUploads.MigrateToKey(newKey);
            songMoves.MigrateToKey(newKey);
        }
        catch
        {
            // Sin AccountId se sigue con la clave legada; nada que romper.
        }
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var t = store.Load();
        return Ok(new
        {
            // Mismo criterio que RequireGoogleSession: evita "conectado" fantasma
            // con un access token vencido y sin refresh.
            isAuthenticated = t is not null && t.HasUsableSession,
            expiresAtUtc = t?.ExpiresAtUtc,
            hasRefreshToken = !string.IsNullOrEmpty(t?.RefreshToken)
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        // Preservar el refresh_token para que UserKey no cambie al reconectar.
        // Solo borramos el access_token y marcamos la sesión como expirada.
        var prev = store.Load();
        if (prev?.RefreshToken is not null)
            store.Save(new GoogleTokenData
            {
                AccessToken = "",
                RefreshToken = prev.RefreshToken,
                ExpiresAtUtc = DateTime.MinValue,
                Scope = prev.Scope
            });
        else
            store.Clear();
        return Ok(new { ok = true });
    }
}
