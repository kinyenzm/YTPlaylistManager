using YTPlaylistManager.Server.Domain.Entities;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Log persistente en JSON (append). Útil para auditar qué hizo la herramienta sobre la cuenta.
/// </summary>
public sealed class OperationLog(IConfiguration cfg)
    : JsonListStore<OperationLogEntry>(cfg, "operations.json")
{
    public void Add(string operation, string details) =>
        Mutate(list => list.Add(new OperationLogEntry(DateTime.UtcNow, operation, details)));
}
