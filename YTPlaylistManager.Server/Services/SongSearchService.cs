using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

public interface ISongSearchService
{
    /// <summary>Busca canciones por videoId (exacto o parcial) en la caché.</summary>
    List<SongSearchResultDto> SearchByVideoId(string videoIdPartial);

    /// <summary>Busca canciones por nombre (substring + fuzzy ≥70%) en la caché.</summary>
    List<SongSearchResultDto> SearchByName(string songNameFuzzy);

    /// <summary>Búsqueda combinada (unión de videoId y nombre).</summary>
    List<SongSearchResultDto> SearchCombined(string? videoIdPartial, string? songNameFuzzy, string searchScope);

    /// <summary>Por playlist: cuántos videos (disponibles) aparecen también en otra lista.</summary>
    Dictionary<string, int> GetDuplicateCountsByPlaylist();

    /// <summary>Historial/ubicaciones de una canción (derivado de la caché).</summary>
    SongMovementLogDto? GetSongTimeline(string videoId);

    /// <summary>Playlists archivadas (no rastreado todavía → vacío).</summary>
    [Obsolete("Use IYouTubeService.GetArchivedPlaylistsAsync instead.")]
    List<PlaylistArchivedInfoDto> GetArchivedPlaylists();
}

/// <summary>
/// Búsqueda bidireccional de canciones sobre la caché local (videoId exacto/parcial + nombre fuzzy).
/// Opera 100% offline sobre <c>PlaylistCacheStore</c> (lista) + <c>PlaylistItemsCacheStore</c> (items).
/// No llama a la YouTube API → 0 cuota.
/// </summary>
public class SongSearchService : ISongSearchService
{
    private const double FuzzyThreshold = 70.0;

    private readonly PlaylistCacheStore _cacheStore;
    private readonly PlaylistItemsCacheStore _itemsCache;
    private readonly ArchivedPlaylistsStore _archivedStore;
    private readonly ILogger<SongSearchService> _logger;

    public SongSearchService(
        PlaylistCacheStore cacheStore,
        PlaylistItemsCacheStore itemsCache,
        ArchivedPlaylistsStore archivedStore,
        ILogger<SongSearchService> logger)
    {
        _cacheStore = cacheStore;
        _itemsCache = itemsCache;
        _archivedStore = archivedStore;
        _logger = logger;
    }

    private readonly record struct FlatItem(string PlaylistId, string PlaylistTitle, PlaylistItemDto Item);

    /// <summary>Aplana todos los items cacheados (por la cuenta actual) en (playlist, item).</summary>
    private List<FlatItem> LoadAllCachedItems()
    {
        var flat = new List<FlatItem>();
        var cache = _cacheStore.Load();
        if (cache?.Playlists is null) return flat;

        foreach (var pl in cache.Playlists)
        {
            var items = _itemsCache.Load(cache.UserKey, pl.Id);
            if (items is null) continue; // playlist aún no escaneada → sin items en caché
            foreach (var it in items)
            {
                // Videos privados/eliminados: fuera de búsquedas y del índice de apariciones
                // (no deben inflar AppearsInCount ni marcar duplicados falsos).
                if (VideoAvailability.IsUnavailable(it.Title)) continue;
                flat.Add(new FlatItem(pl.Id, pl.Title, it));
            }
        }
        return flat;
    }

    /// <summary>videoId → ids de playlists distintas donde aparece.</summary>
    private static Dictionary<string, List<string>> AppearanceIndex(List<FlatItem> flat) =>
        flat.Where(x => !string.IsNullOrEmpty(x.Item.VideoId))
            .GroupBy(x => x.Item.VideoId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlaylistId).Distinct().ToList());

    private static SongSearchResultDto ToResult(FlatItem x, Dictionary<string, List<string>> appearance)
    {
        var ids = appearance.TryGetValue(x.Item.VideoId, out var list) ? list : [];
        return new SongSearchResultDto(
            x.Item.VideoId,
            x.Item.Title,
            x.Item.ChannelTitle ?? "",
            x.PlaylistId, x.PlaylistTitle, x.Item.Position,   // "original" = ubicación actual (no hay tracking de merge)
            x.PlaylistId, x.PlaylistTitle, x.Item.Position,   // "current"
            ids.Count,
            ids,
            ids.Count > 1,                                    // IsDuplicate: aparece en 2+ playlists
            false,                                            // WasMerged
            null);                                            // MergeId
    }

    public List<SongSearchResultDto> SearchByVideoId(string videoIdPartial)
    {
        var p = videoIdPartial?.Trim();
        if (string.IsNullOrWhiteSpace(p)) return [];

        var flat = LoadAllCachedItems();
        var appearance = AppearanceIndex(flat);
        return flat
            .Where(x => x.Item.VideoId.Contains(p, StringComparison.OrdinalIgnoreCase))
            .Select(x => ToResult(x, appearance))
            .ToList();
    }

    public List<SongSearchResultDto> SearchByName(string songNameFuzzy)
    {
        var raw = songNameFuzzy?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var q = NormalizeText(raw);
        var flat = LoadAllCachedItems();
        var appearance = AppearanceIndex(flat);

        return flat
            .Where(x =>
            {
                var title = NormalizeText(x.Item.Title);
                return title.Contains(q, StringComparison.Ordinal)
                    || CalculateFuzzySimilarity(title, q) >= FuzzyThreshold;
            })
            .Select(x => ToResult(x, appearance))
            .ToList();
    }

    public List<SongSearchResultDto> SearchCombined(string? videoIdPartial, string? songNameFuzzy, string searchScope)
    {
        var byId = string.IsNullOrWhiteSpace(videoIdPartial) ? [] : SearchByVideoId(videoIdPartial);
        var byName = string.IsNullOrWhiteSpace(songNameFuzzy) ? [] : SearchByName(songNameFuzzy);

        // Unión sin repetir la misma ocurrencia (videoId + playlist).
        var union = byId
            .Concat(byName)
            .GroupBy(r => (r.VideoId, r.CurrentPlaylistId))
            .Select(g => g.First());

        // Scope sobre la playlist donde está la ocurrencia: active = no archivadas,
        // archived = solo archivadas, all (o valor desconocido) = sin filtro.
        var scope = (searchScope ?? "all").ToLowerInvariant();
        if (scope is "active" or "archived")
        {
            var archivedIds = _archivedStore.LoadAll().Select(e => e.Id).ToHashSet();
            union = scope == "active"
                ? union.Where(r => r.CurrentPlaylistId is null || !archivedIds.Contains(r.CurrentPlaylistId))
                : union.Where(r => r.CurrentPlaylistId is not null && archivedIds.Contains(r.CurrentPlaylistId));
        }

        return union.ToList();
    }

    public Dictionary<string, int> GetDuplicateCountsByPlaylist()
    {
        var flat = LoadAllCachedItems();
        var appearance = AppearanceIndex(flat);
        return flat
            .Where(x => !string.IsNullOrEmpty(x.Item.VideoId)
                        && appearance.TryGetValue(x.Item.VideoId, out var ids) && ids.Count > 1)
            .GroupBy(x => x.PlaylistId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item.VideoId).Distinct().Count());
    }

    public SongMovementLogDto? GetSongTimeline(string videoId)
    {
        var flat = LoadAllCachedItems();
        var hits = flat.Where(x => string.Equals(x.Item.VideoId, videoId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) return null;

        var events = hits
            .Select(h => new SongMovementEventDto(
                DateTime.UtcNow, "cached", h.PlaylistId, h.PlaylistTitle, h.Item.Position, null, "Presente en caché"))
            .ToList();

        return new SongMovementLogDto(videoId, hits[0].Item.Title, events);
    }

    [Obsolete("Use IYouTubeService.GetArchivedPlaylistsAsync instead.")]
    public List<PlaylistArchivedInfoDto> GetArchivedPlaylists() => [];

    // ── Helpers de texto/fuzzy ──

    private static int LevenshteinDistance(string s1, string s2)
    {
        if (s1.Length == 0) return s2.Length;
        if (s2.Length == 0) return s1.Length;

        var d = new int[s1.Length + 1, s2.Length + 1];
        for (int i = 0; i <= s1.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= s2.Length; j++) d[0, j] = j;

        for (int i = 1; i <= s1.Length; i++)
            for (int j = 1; j <= s2.Length; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }

        return d[s1.Length, s2.Length];
    }

    private static string NormalizeText(string text)
    {
        var chars = (text ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in chars)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
    }

    private static double CalculateFuzzySimilarity(string s1, string s2)
    {
        int maxLen = Math.Max(s1.Length, s2.Length);
        if (maxLen == 0) return 100;
        return (1.0 - (double)LevenshteinDistance(s1, s2) / maxLen) * 100;
    }
}
