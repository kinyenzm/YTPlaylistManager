namespace YTPlaylistManager.Server.Domain.Exceptions;

/// <summary>El clasificador IA no está disponible (sin API key, sin red, o todos los modelos fallaron).</summary>
public sealed class AiUnavailableException(string message) : Exception(message);
