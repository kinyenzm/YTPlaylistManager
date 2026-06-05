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
    GoogleTokenStore store) : ControllerBase
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
            + "&prompt=consent";

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
        }

        store.Save(token);

        // Redirige al frontend
        var ui = cfg["Cors:AllowedOrigins:0"] ?? "http://localhost:4200";
        return Redirect($"{ui}/?auth=ok");
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var t = store.Load();
        return Ok(new
        {
            isAuthenticated = t is not null && !string.IsNullOrEmpty(t.AccessToken),
            expiresAtUtc = t?.ExpiresAtUtc,
            hasRefreshToken = !string.IsNullOrEmpty(t?.RefreshToken)
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        store.Clear();
        return Ok(new { ok = true });
    }
}
