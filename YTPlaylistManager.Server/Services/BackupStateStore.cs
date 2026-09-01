namespace YTPlaylistManager.Server.Services;

/// <summary>Estado local del respaldo automático (propio de esta máquina; no se respalda).</summary>
public sealed class BackupState
{
    public DateTime? LastDriveBackupUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
}

public sealed class BackupStateStore(IConfiguration cfg)
    : JsonFileStore(cfg, "backup-state.json")
{
    public BackupState Load()
    {
        lock (Sync) return Read<BackupState>() ?? new BackupState();
    }

    public void Save(BackupState state)
    {
        lock (Sync) Write(state);
    }
}
