namespace YTPlaylistManager.Server.Domain.Exceptions;

/// <summary>
/// No hay sesión Google activa para la operación solicitada. El middleware la mapea a 401.
/// Defensiva: el caso normal se corta antes con <c>RequireGoogleSessionAttribute</c> (sin lanzar).
/// </summary>
public sealed class NotAuthenticatedException(string message) : Exception(message);
