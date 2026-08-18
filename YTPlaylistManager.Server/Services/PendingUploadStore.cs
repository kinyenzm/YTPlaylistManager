using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Uniones aplicadas en local pendientes de subir a YouTube. Persistido en JSON
/// local (sobrevive reinicios). Keyed por cuenta (UserKey) dentro de cada registro.
/// </summary>
public sealed class PendingUploadStore(IConfiguration cfg)
    : PendingPlanStore<PendingUpload>(cfg, "pending-uploads.json");
