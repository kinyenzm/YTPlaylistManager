using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Reasignaciones de canciones aplicadas en local pendientes de subir a YouTube.
/// Persistido en JSON (sobrevive reinicios → la subida es reanudable).
/// </summary>
public sealed class PendingSongMoveStore(IConfiguration cfg)
    : PendingPlanStore<PendingSongMove>(cfg, "pending-song-moves.json");
