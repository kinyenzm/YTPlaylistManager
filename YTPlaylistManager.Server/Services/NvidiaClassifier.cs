using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YTPlaylistManager.Server.Domain.Exceptions;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Clasificador que usa modelos hospedados en NVIDIA (build.nvidia.com / NVIDIA NIM).
/// Implementa una estrategia de tolerancia a fallos recorriendo modelos del tier free si el principal falla.
/// </summary>
public class NvidiaClassifier : IAiClassifier
{
    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NvidiaClassifier> _logger;

    public NvidiaClassifier(IConfiguration cfg, IHttpClientFactory httpFactory, ILogger<NvidiaClassifier> logger)
    {
        _cfg = cfg;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<Dictionary<string, List<ClassifiedSongDto>>> ClassifyAsync(
        IEnumerable<PlaylistItemDto> items,
        string mode,
        CancellationToken ct = default)
    {
        var list = items.ToList();
        var apiKey = _cfg["Ai:NvidiaApiKey"];
        var baseUrl = _cfg["Ai:NvidiaBaseUrl"];

        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "https://integrate.api.nvidia.com/v1/";
        if (!baseUrl.EndsWith("/")) baseUrl += "/";

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("TU_"))
        {
            _logger.LogWarning("Sin API key de NVIDIA configurada (Ai:NvidiaApiKey).");
            throw new AiUnavailableException("Falta la API key de NVIDIA (Ai:NvidiaApiKey).");
        }

        // 1. Armar la lista de modelos a intentar (Principal + Alternativos)
        var primaryModel = _cfg["Ai:NvidiaModel"] ?? "minimaxai/minimax-m2.7";
        var modelsToTry = new List<string> { primaryModel };

        // Intentar leer fallbacks desde appsettings, si no hay, usamos una lista interna robusta
        var fallbackModels = _cfg.GetSection("Ai:NvidiaFallbackModels").Get<List<string>>();
        if (fallbackModels != null && fallbackModels.Any())
        {
            modelsToTry.AddRange(fallbackModels);
        }
        else
        {
            modelsToTry.Add("meta/llama-3.3-70b-instruct");
        }

        var prompt = BuildPrompt(list, mode);
        var http = _httpFactory.CreateClient("nvidia");
        http.BaseAddress = new Uri(baseUrl);
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        // 2. Bucle de tolerancia a fallos
        foreach (var model in modelsToTry)
        {
            try
            {
                _logger.LogInformation("Intentando clasificar con el modelo NVIDIA: {Model}", model);

                var body = new
                {
                    model,
                    messages = new object[]
                    {
                        new { role = "system", content = "Eres un clasificador musical. Responde SIEMPRE en JSON válido sin texto adicional." },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.2, // Mantener baja temperatura asegura respuestas estructuradas y estables
                    top_p = 0.7,
                    max_tokens = 4000,
                    stream = false
                };

                var resp = await http.PostAsync("chat/completions",
                    new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

                var raw = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("El modelo {Model} devolvió un código de error {Status}. Buscando alternativa...", model, resp.StatusCode);
                    continue; // Salta al siguiente modelo en el foreach
                }

                using var doc = JsonDocument.Parse(raw);
                var text = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                // Intentamos parsear. Si devuelve null, significa que el formato JSON del LLM fue malo.
                var parsedResult = ParseGroups(text, list);
                if (parsedResult != null)
                {
                    _logger.LogInformation("¡Clasificación completada con éxito usando {Model}!", model);
                    return parsedResult;
                }

                _logger.LogWarning("El modelo {Model} respondió HTTP 200 pero generó un JSON inválido. Saltando al siguiente...", model);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error de red o conexión al intentar usar el modelo {Model}.", model);
                // Continuar al siguiente modelo sin romper la ejecución de la app
            }
        }

        // 3. Todos los modelos fallaron (red caída, timeout, modelos no disponibles):
        // error explícito para que la UI avise en vez de devolver una clasificación falsa.
        _logger.LogError("Todos los modelos del catálogo de NVIDIA fallaron o no están disponibles.");
        throw new AiUnavailableException("Ningún modelo de IA respondió (revisa conectividad y configuración).");
    }

    private static string BuildPrompt(List<PlaylistItemDto> items, string mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Clasifica las siguientes canciones por {mode} (en español, máximo 10 categorías).");
        sb.AppendLine("Responde ÚNICAMENTE con un objeto JSON con la forma:");
        sb.AppendLine("{\"videoId1\": \"Categoria\", \"videoId2\": \"Categoria\", ...}");
        sb.AppendLine("Sin explicaciones, sin markdown, sin code fences. Solo el JSON.");
        sb.AppendLine();
        sb.AppendLine("Canciones (videoId | título | canal):");
        foreach (var it in items)
        {
            sb.AppendLine($"- {it.VideoId} | {it.Title} | {it.ChannelTitle}");
        }
        return sb.ToString();
    }

    // AHORA DEVUELVE NULL SI EL JSON ES INVÁLIDO PARA DARLE OPORTUNIDAD A OTRO MODELO
    private static Dictionary<string, List<ClassifiedSongDto>>? ParseGroups(string text, List<PlaylistItemDto> items)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var firstNl = text.IndexOf('\n');
            if (firstNl > 0) text = text[(firstNl + 1)..];
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0) text = text[..lastFence];
        }

        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
            return null;

        var jsonChunk = text.Substring(jsonStart, jsonEnd - jsonStart + 1);
        Dictionary<string, string>? map;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonChunk);
        }
        catch
        {
            return null;
        }

        if (map is null) return null;

        var result = new Dictionary<string, List<ClassifiedSongDto>>();
        foreach (var it in items)
        {
            var group = map.TryGetValue(it.VideoId, out var g) && !string.IsNullOrWhiteSpace(g)
                ? g : "Sin clasificar";
            if (!result.ContainsKey(group)) result[group] = new();
            result[group].Add(new ClassifiedSongDto(it.VideoId, it.Title, group));
        }
        return result;
    }

}