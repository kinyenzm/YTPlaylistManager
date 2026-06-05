using System.Text.Json;
using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Almacén simple en JSON del token OAuth del usuario.
/// Para una herramienta personal/local es suficiente. NO usar así en producción multi-usuario.
/// </summary>
public class GoogleTokenStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public GoogleTokenStore(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "google-token.json");
    }

    public GoogleTokenData? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return null;
            var raw = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<GoogleTokenData>(raw);
        }
    }

    public void Save(GoogleTokenData data)
    {
        lock (_lock)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }
}
