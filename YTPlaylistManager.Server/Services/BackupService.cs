using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Apis.Download;
using Google.Apis.Upload;
using YTPlaylistManager.Server.DTOs;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Respaldo del registro local (carpeta de datos) en un solo .json legible:
/// exportar/importar como archivo y subir/restaurar contra el appDataFolder de
/// Google Drive del usuario. Nunca incluye credenciales (google-token.json) ni
/// estado propio de la máquina (quota.json, backup-state.json).
/// </summary>
public sealed class BackupService(
    IConfiguration cfg,
    GoogleTokenStore tokenStore,
    YouTubeClientFactory clientFactory,
    PlaylistItemsCacheStore itemsCache,
    ActivityBroadcaster activity,
    UserKeyMigration migration,
    BackupStateStore backupState,
    ILogger<BackupService> logger)
{
    public const string DriveScope = "https://www.googleapis.com/auth/drive.appdata";

    private const string DriveFileName = "ytpm-backup.json";
    private const int FormatVersion = 1;

    /// <summary>Lista blanca cerrada: solo estos archivos entran y salen de un respaldo.</summary>
    public static readonly string[] StoreFiles =
    [
        "playlist-cache.json",
        "items-cache.json",
        "items-cache.json.bak",
        "archived-playlists.json",
        "merge-reviews.json",
        "operations.json",
        "activity-log.json",
        "playlist-touch.json",
        "pending-uploads.json",
        "pending-song-moves.json",
    ];

    private string DataFolder => JsonFileStore.ResolveDataFolder(cfg);

    public bool HasDriveScope() =>
        (tokenStore.Load()?.Scope ?? "").Split(' ').Contains(DriveScope);

    /// <summary>Fecha del cambio más reciente en los archivos respaldables (para el worker).</summary>
    public DateTime? LastLocalChangeUtc()
    {
        DateTime? max = null;
        foreach (var name in StoreFiles)
        {
            var path = Path.Combine(DataFolder, name);
            if (!File.Exists(path)) continue;
            var t = File.GetLastWriteTimeUtc(path);
            if (max is null || t > max) max = t;
        }
        return max;
    }

    /// <summary>Arma el respaldo como un solo JSON: { format, version, exportedAtUtc, stores: { archivo → contenido } }.</summary>
    public string ExportJson()
    {
        var stores = new JsonObject();
        JsonFileStore.RunExclusive(() =>
        {
            foreach (var name in StoreFiles)
            {
                var path = Path.Combine(DataFolder, name);
                if (!File.Exists(path)) continue;
                stores[name] = JsonNode.Parse(File.ReadAllText(path));
            }
        });

        var root = new JsonObject
        {
            ["format"] = "ytpm-backup",
            ["version"] = FormatVersion,
            ["exportedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["stores"] = stores,
        };
        return root.ToJsonString();
    }

    /// <summary>
    /// Restaura un respaldo: valida TODO antes de tocar la carpeta de datos, guarda una
    /// copia previa del estado actual, reemplaza los archivos como unidad (bajo el lock
    /// global), recarga los stores con estado en memoria y migra las claves de cuenta a
    /// la sesión actual.
    /// </summary>
    public BackupImportResultDto Import(Stream json, string currentUserKey)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new ArgumentException("El archivo no es JSON válido.");
        }

        if (root is not JsonObject obj || (string?)obj["format"] != "ytpm-backup")
            throw new ArgumentException("El archivo no es un respaldo de esta aplicación.");
        var version = (int?)obj["version"] ?? 0;
        if (version is < 1 or > FormatVersion)
            throw new ArgumentException($"Versión de respaldo no soportada ({version}).");
        if (obj["stores"] is not JsonObject stores || stores.Count == 0)
            throw new ArgumentException("El respaldo no trae datos.");

        // Solo claves de la lista blanca: nada de rutas ni secciones desconocidas.
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, node) in stores)
        {
            if (!StoreFiles.Contains(name))
                throw new ArgumentException($"El respaldo trae una sección desconocida: {name}");
            if (node is not null) payload[name] = node.ToJsonString();
        }
        if (payload.Count == 0)
            throw new ArgumentException("El respaldo no trae datos.");

        JsonFileStore.RunExclusive(() =>
        {
            SnapshotCurrentUnlocked();
            foreach (var (name, text) in payload)
            {
                // Escritura atómica por archivo; el conjunto va bajo el lock global.
                var tmp = Path.Combine(DataFolder, name + ".importing");
                File.WriteAllText(tmp, text);
                File.Move(tmp, Path.Combine(DataFolder, name), overwrite: true);
            }
            itemsCache.Reload();
            activity.Reload();
            migration.MigrateAll(currentUserKey);
        });

        // Los mtimes recién escritos no deben disparar una re-subida inmediata del worker.
        var state = backupState.Load();
        state.LastDriveBackupUtc = DateTime.UtcNow;
        backupState.Save(state);

        logger.LogInformation("Respaldo importado: {Count} archivos restaurados.", payload.Count);
        return new BackupImportResultDto(payload.Count);
    }

    /// <summary>Copia del estado actual antes de pisarlo (data/backups/pre-import-*).</summary>
    private void SnapshotCurrentUnlocked()
    {
        var dir = Path.Combine(DataFolder, "backups", $"pre-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);
        foreach (var name in StoreFiles)
        {
            var src = Path.Combine(DataFolder, name);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dir, name), overwrite: true);
        }
    }

    // ── Google Drive (appDataFolder: carpeta oculta por-app, atada a la cuenta) ──

    private async Task<DriveFile?> FindBackupAsync(Google.Apis.Drive.v3.DriveService drive, CancellationToken ct)
    {
        var list = drive.Files.List();
        list.Spaces = "appDataFolder";
        list.Q = $"name='{DriveFileName}' and trashed=false";
        list.Fields = "files(id,modifiedTime,size)";
        var resp = await list.ExecuteAsync(ct);
        return resp.Files?.FirstOrDefault();
    }

    public async Task<BackupStatusDto> StatusAsync(CancellationToken ct)
    {
        var scope = HasDriveScope();
        var state = backupState.Load();
        DateTime? modified = null;
        long? size = null;
        if (scope)
        {
            try
            {
                var f = await FindBackupAsync(clientFactory.BuildDriveClient(), ct);
                modified = f?.ModifiedTimeDateTimeOffset?.UtcDateTime;
                size = f?.Size;
            }
            catch (Google.GoogleApiException ex)
            {
                // El estado no debe romperse por Drive: se informa sin fecha remota.
                logger.LogWarning(ex, "No se pudo consultar el respaldo en Drive.");
            }
        }
        return new BackupStatusDto(scope, state.LastDriveBackupUtc, modified, size);
    }

    /// <summary>Sube (o sobrescribe) el único respaldo en Drive.</summary>
    public async Task UploadToDriveAsync(CancellationToken ct)
    {
        var drive = clientFactory.BuildDriveClient();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ExportJson()));

        var existing = await FindBackupAsync(drive, ct);
        // UploadAsync no lanza: hay que mirar el progreso y relanzar su excepción.
        if (existing is not null)
        {
            var progress = await drive.Files.Update(new DriveFile(), existing.Id, stream, "application/json").UploadAsync(ct);
            if (progress.Status != UploadStatus.Completed)
                throw progress.Exception ?? new IOException("La subida a Drive no terminó.");
        }
        else
        {
            var meta = new DriveFile { Name = DriveFileName, Parents = ["appDataFolder"] };
            var progress = await drive.Files.Create(meta, stream, "application/json").UploadAsync(ct);
            if (progress.Status != UploadStatus.Completed)
                throw progress.Exception ?? new IOException("La subida a Drive no terminó.");
        }

        var state = backupState.Load();
        state.LastDriveBackupUtc = DateTime.UtcNow;
        state.LastFailureUtc = null;
        backupState.Save(state);
    }

    /// <summary>Descarga el respaldo de Drive y lo restaura (mismo camino que importar un archivo).</summary>
    public async Task<BackupImportResultDto> RestoreFromDriveAsync(string currentUserKey, CancellationToken ct)
    {
        var drive = clientFactory.BuildDriveClient();
        var f = await FindBackupAsync(drive, ct)
            ?? throw new ArgumentException("Todavía no hay ningún respaldo en Drive.");

        using var ms = new MemoryStream();
        var progress = await drive.Files.Get(f.Id).DownloadAsync(ms, ct);
        if (progress.Status != DownloadStatus.Completed)
            throw progress.Exception ?? new IOException("La descarga desde Drive no terminó.");

        ms.Position = 0;
        return Import(ms, currentUserKey);
    }
}
