namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Reasigna todos los stores keyed por cuenta a una clave nueva. La app es
/// single-user (un solo archivo de token), así que toda clave preexistente
/// pertenece al mismo humano. Lo usan el login (cuando resuelve el AccountId)
/// y la importación de respaldos (los archivos restaurados pueden venir de una
/// sesión con otra semilla de clave).
/// </summary>
public sealed class UserKeyMigration(
    PlaylistItemsCacheStore itemsCache,
    PlaylistCacheStore playlistCache,
    PendingUploadStore pendingUploads,
    PendingSongMoveStore songMoves)
{
    public void MigrateAll(string newKey)
    {
        itemsCache.MigrateToKey(newKey);
        playlistCache.MigrateToKey(newKey);
        pendingUploads.MigrateToKey(newKey);
        songMoves.MigrateToKey(newKey);
    }
}
