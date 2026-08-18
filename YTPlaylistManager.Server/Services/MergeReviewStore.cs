using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Historial de planes de merge en revisión (solo lectura: alimenta la pestaña
/// Revisiones). El flujo que los escribía fue reemplazado por el panel de
/// pendientes; los registros existentes se conservan como histórico.
/// </summary>
public sealed class MergeReviewStore(IConfiguration cfg)
    : JsonListStore<MergeReviewPlan>(cfg, "merge-reviews.json");
