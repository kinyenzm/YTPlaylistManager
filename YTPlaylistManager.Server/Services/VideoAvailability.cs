namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Detección de items de playlist sin video accesible. La YouTube API devuelve
/// estos títulos literales (siempre en inglés) cuando el video es privado o fue
/// eliminado; el item no tiene snippet real y no debe contar como canción en
/// detecciones de duplicados ni en búsquedas.
/// </summary>
public static class VideoAvailability
{
    public static bool IsUnavailable(string? title)
    {
        var t = title?.Trim();
        return string.Equals(t, "Private video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Deleted video", StringComparison.OrdinalIgnoreCase);
    }
}
