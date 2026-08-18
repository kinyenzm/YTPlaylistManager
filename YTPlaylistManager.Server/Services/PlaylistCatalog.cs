using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Consultas compartidas sobre la caché local de playlists: títulos, vigencia y
/// utilidades que antes estaban repetidas entre los servicios.
/// </summary>
public sealed class PlaylistCatalog(PlaylistCacheStore cacheStore)
{
    /// <summary>Título de una playlist según la caché de la lista; el id si no está.</summary>
    public string TitleOf(string playlistId) =>
        cacheStore.Load()?.Playlists.FirstOrDefault(p => p.Id == playlistId)?.Title ?? playlistId;

    /// <summary>
    /// Ids de playlists vigentes según la caché de la lista (0 cuota). Devuelve null si
    /// la caché aún no existe: en ese caso NO se puede afirmar que una lista falte, así
    /// que las validaciones deben dejar pasar en vez de bloquear por falta de datos.
    /// </summary>
    public HashSet<string>? KnownPlaylistIds()
    {
        var cache = cacheStore.Load();
        return cache is null ? null : cache.Playlists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True solo si sabemos con certeza que la playlist ya no existe.</summary>
    public bool IsKnownMissing(string playlistId)
    {
        // Las listas del canal (Favoritos, Ver más tarde, Me gusta, Mezcla) nunca aparecen
        // en playlists.list?mine=true, así que faltar de la caché no prueba nada sobre
        // ellas: existen, solo que por otra vía.
        if (IsSpecialPlaylist(playlistId)) return false;
        var known = KnownPlaylistIds();
        return known is not null && !known.Contains(playlistId);
    }

    /// <summary>
    /// Playlists automáticas de YouTube (FL=Favoritos, WL=Ver más tarde, LL=Me gusta,
    /// RD=Mezcla): no aparecen en el listado del dueño y la API no permite escribirlas.
    /// </summary>
    public static bool IsSpecialPlaylist(string id) =>
        id.StartsWith("FL", StringComparison.Ordinal) ||
        id.StartsWith("WL", StringComparison.Ordinal) ||
        id.StartsWith("LL", StringComparison.Ordinal) ||
        id.StartsWith("RD", StringComparison.Ordinal);

    /// <summary>Posición siguiente al agregar al final de una lista de items.</summary>
    public static int NextPosition(List<PlaylistItemDto> items) =>
        items.Count == 0 ? 0 : items.Max(i => i.Position) + 1;

    /// <summary>Quita playlists de la caché de la lista (tras borrarlas en YouTube).</summary>
    public void RemoveFromListCache(IEnumerable<string> ids)
    {
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        if (idSet.Count == 0) return;
        var cache = cacheStore.Load();
        if (cache is null) return;
        var filtered = cache.Playlists.Where(p => !idSet.Contains(p.Id)).ToList();
        if (filtered.Count != cache.Playlists.Count)
            cacheStore.Save(new PlaylistCache { UserKey = cache.UserKey, CachedAtUtc = cache.CachedAtUtc, Playlists = filtered });
    }
}
