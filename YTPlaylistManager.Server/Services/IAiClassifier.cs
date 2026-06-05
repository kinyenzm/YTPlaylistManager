using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

public interface IAiClassifier
{
    /// <summary>
    /// Recibe una lista de items y devuelve un diccionario "grupo -> items" según el mode.
    /// Mode: "genre" | "mood" | "decade".
    /// </summary>
    Task<Dictionary<string, List<ClassifiedSongDto>>> ClassifyAsync(
        IEnumerable<PlaylistItemDto> items,
        string mode,
        CancellationToken ct = default);
}
