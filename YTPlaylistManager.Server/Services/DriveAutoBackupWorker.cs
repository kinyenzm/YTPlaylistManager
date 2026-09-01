namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Respaldo automático best-effort: cada minuto mira si los archivos respaldables
/// cambiaron después del último respaldo y llevan un rato en calma, y si es así
/// sube el respaldo a Drive. Nunca toca la red sin scope de Drive o sin sesión
/// usable, y tras un fallo espera antes de reintentar.
/// </summary>
public sealed class DriveAutoBackupWorker(
    IServiceScopeFactory scopeFactory,
    BackupStateStore backupState,
    ILogger<DriveAutoBackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Respaldo automático: fallo inesperado del ciclo.");
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var state = backupState.Load();
        if (state.LastFailureUtc is not null && DateTime.UtcNow - state.LastFailureUtc < FailureBackoff)
            return;

        using var scope = scopeFactory.CreateScope();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        var tokenStore = scope.ServiceProvider.GetRequiredService<GoogleTokenStore>();

        if (!backup.HasDriveScope()) return;                        // sin permiso: ni un byte de red
        var token = tokenStore.Load();
        if (token is null || !token.HasUsableSession) return;       // sesión muerta: idem

        var lastChange = backup.LastLocalChangeUtc();
        if (lastChange is null) return;
        if (state.LastDriveBackupUtc is not null && lastChange <= state.LastDriveBackupUtc) return;
        if (DateTime.UtcNow - lastChange < QuietPeriod) return;     // esperar a que termine la ráfaga

        try
        {
            await backup.UploadToDriveAsync(ct);
            logger.LogInformation("Respaldo automático subido a Drive.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Respaldo automático: no se pudo subir; próximo intento en {Min} min.", FailureBackoff.TotalMinutes);
            var st = backupState.Load();
            st.LastFailureUtc = DateTime.UtcNow;
            backupState.Save(st);
        }
    }
}
