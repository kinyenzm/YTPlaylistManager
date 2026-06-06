using Scalar.AspNetCore;
using YTPlaylistManager.Server.Middleware;
using YTPlaylistManager.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<GoogleTokenStore>();
builder.Services.AddSingleton<OperationLog>();
builder.Services.AddSingleton<MergeReviewStore>();
builder.Services.AddSingleton<PendingUploadStore>();
builder.Services.AddSingleton<PendingSongMoveStore>();
builder.Services.AddSingleton<ArchivedPlaylistsStore>();
builder.Services.AddSingleton<PlaylistCacheStore>();
builder.Services.AddSingleton<PlaylistItemsCacheStore>();
builder.Services.AddSingleton<ApiKeyPool>();
builder.Services.AddScoped<IYouTubeService, YouTubeService>();
builder.Services.AddScoped<ISongSearchService, SongSearchService>();

// Selección del proveedor de IA según appsettings: Ai:Provider = nvidia (por defecto).
var aiProvider = (builder.Configuration["Ai:Provider"] ?? "nvidia").ToLowerInvariant();
switch (aiProvider)
{
    case "nvidia":
    default:
        builder.Services.AddScoped<IAiClassifier, NvidiaClassifier>();
        break;
}

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };

builder.Services.AddCors(opts =>
{
    opts.AddDefaultPolicy(p => p
        .WithOrigins(origins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

// ── Middleware pipeline ──

app.UseMiddleware<GlobalExceptionMiddleware>();

// Sirve el SPA Angular compilado desde wwwroot (en prod/Docker). En dev no hay
// wwwroot y el frontend corre en su propio dev-server (:4200) vía proxy.
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    // OpenAPI nativo de .NET 10 (documento en /openapi/v1.json) + UI Scalar en /scalar/v1
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
        options.WithTitle("YTPlaylistManager API"));
}

app.UseCors();

app.MapControllers();

// Cualquier ruta no-API cae al index.html del SPA (client-side routing).
app.MapFallbackToFile("index.html");

await app.RunAsync();
