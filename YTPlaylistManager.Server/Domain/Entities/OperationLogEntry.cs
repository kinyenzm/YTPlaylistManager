namespace YTPlaylistManager.Server.Domain.Entities;

/// <summary>
/// Entrada del log de operaciones realizadas sobre la cuenta (borrar, unir, crear playlist).
/// </summary>
public record OperationLogEntry(
    DateTime At,
    string Operation,
    string Details
);