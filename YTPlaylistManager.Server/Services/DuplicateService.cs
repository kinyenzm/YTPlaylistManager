using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Detección y limpieza de canciones repetidas: dentro de una lista (por videoId y
/// por título normalizado) y entre listas. La lectura de items la delega en
/// <see cref="IYouTubeService"/>, que resuelve caché y cuota.
/// </summary>
public sealed class DuplicateService(
    IYouTubeService youtube,
    YouTubeClientFactory clientFactory,
    PlaylistItemsCacheStore itemsCache,
    PlaylistTouchStore touchStore,
    QuotaTracker quota,
    OperationLog log,
    ILogger<DuplicateService> logger)
{
    public async Task<DuplicateReportDto> FindDuplicatesAsync(string playlistId, CancellationToken ct = default)
    {
        // El título es decorativo: si su fetch falla (cuota, 404, red) se usa el id
        // como fallback en vez de tumbar toda la detección de duplicados.
        var playlistTitle = playlistId;
        try
        {
            var yt = clientFactory.BuildClient();
            var playlistReq = yt.Playlists.List("snippet");
            playlistReq.Id = playlistId;
            var pResp = await playlistReq.ExecuteAsync(ct);
            quota.Add(1);
            playlistTitle = pResp.Items.FirstOrDefault()?.Snippet.Title ?? playlistId;
        }
        catch (Google.GoogleApiException ex)
        {
            if (QuotaTracker.IsQuotaError(ex)) quota.MarkExhausted();
            logger.LogWarning(ex, "No se pudo leer el título de {Playlist}; se usa el id.", playlistId);
        }

        // Detección precisa: re-leemos los items DESDE YouTube (no de la caché, que puede
        // estar desincronizada por remociones locales no subidas). Esto refresca la caché.
        // Si ESTO falla, la excepción sube al middleware global (403 cuota / 404 / etc.).
        var items = await youtube.GetPlaylistItemsAsync(playlistId, ct, forceRefresh: true);

        var groups = new List<DuplicateGroupDto>();

        // Por videoId
        foreach (var g in items
                     .Where(x => !string.IsNullOrEmpty(x.VideoId) && !VideoAvailability.IsUnavailable(x.Title))
                     .GroupBy(x => x.VideoId)
                     .Where(g => g.Count() > 1))
        {
            groups.Add(new DuplicateGroupDto(g.Key, "videoId", g.OrderBy(x => x.Position).ToList()));
        }

        // Por título normalizado (capta "misma canción con distinto video"). Se colapsa a un
        // representante por videoId: las copias exactas ya las cubre el grupo de arriba, y sin
        // este colapso el caso «2 copias del video A + 1 del video B» escondía al B — sus
        // compañeros de título quedaban "ya marcados" y un grupo de 1 se descartaba.
        foreach (var g in items
                     .Where(x => !string.IsNullOrEmpty(x.VideoId) && !VideoAvailability.IsUnavailable(x.Title))
                     .GroupBy(x => Normalize(x.Title))
                     .Where(g => !string.IsNullOrWhiteSpace(g.Key)))
        {
            var byVideo = g.OrderBy(x => x.Position).DistinctBy(x => x.VideoId).ToList();
            if (byVideo.Count > 1)
                groups.Add(new DuplicateGroupDto(g.Key, "normalizedTitle", byVideo));
        }

        var dupCount = groups.Sum(g => g.Items.Count - 1);

        return new DuplicateReportDto(playlistId, playlistTitle, items.Count, dupCount, groups);
    }

    public async Task<CrossDuplicateReportDto> FindCrossDuplicatesAsync(CancellationToken ct = default, bool forceRefresh = false)
    {
        var playlists = await youtube.GetMyPlaylistsAsync(ct);

        var map = new Dictionary<string, (string Title, Dictionary<string, string> Playlists)>();
        int scanned = 0, failed = 0;
        Google.GoogleApiException? lastError = null;

        foreach (var pl in playlists)
        {
            List<PlaylistItemDto> items;
            try
            {
                items = await youtube.GetPlaylistItemsAsync(pl.Id, ct, forceRefresh);
                scanned++;
            }
            catch (Google.GoogleApiException ex)
            {
                failed++;
                lastError = ex;
                logger.LogWarning(ex, "No se pudo leer la playlist {Playlist} ({Status}); se omite.", pl.Id, ex.HttpStatusCode);
                continue;
            }

            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.VideoId)) continue;
                if (VideoAvailability.IsUnavailable(it.Title)) continue;
                if (!map.TryGetValue(it.VideoId, out var entry))
                {
                    entry = (it.Title, new Dictionary<string, string>());
                    map[it.VideoId] = entry;
                }
                entry.Playlists[pl.Id] = pl.Title; // distinct por playlistId (Dictionary compartido por referencia)
            }
        }

        // No se pudo leer NINGUNA playlist (típico: cuota agotada) → propagar el motivo real
        // en vez de devolver "0 repetidos", que sería engañoso.
        if (scanned == 0 && lastError is not null) throw lastError;

        var groups = map
            .Where(kv => kv.Value.Playlists.Count > 1)
            .Select(kv => new CrossDuplicateDto(
                kv.Key,
                kv.Value.Title,
                kv.Value.Playlists.Count,
                kv.Value.Playlists.Select(p => new CrossPlaylistRefDto(p.Key, p.Value)).ToList()))
            .OrderByDescending(g => g.PlaylistCount)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CrossDuplicateReportDto(playlists.Count, groups.Count, groups, scanned, failed);
    }

    public RemoveDuplicatesResultDto RemoveDuplicates(RemoveDuplicatesRequest req)
    {
        var userKey = clientFactory.CurrentUserKey();

        // LOCAL: operamos sobre la caché. Si la playlist no está cacheada no podemos
        // deduplicar (no sabemos qué hay). El usuario debe abrir la playlist primero
        // (lo que la cachea) y volver a intentar.
        var items = itemsCache.Load(userKey, req.PlaylistId)
            ?? throw new InvalidOperationException(
                "La playlist no está en caché. Abrila una vez desde la app para que se cargue y volvé a intentar.");

        // Por título nunca se tocan los videos privados/eliminados (su título placeholder es
        // idéntico entre canciones distintas) ni los títulos que normalizan a vacío:
        // agruparlos borraría canciones reales que solo comparten el placeholder.
        var untouchable = req.Strategy == "normalizedTitle"
            ? items.Where(x => VideoAvailability.IsUnavailable(x.Title) || string.IsNullOrWhiteSpace(Normalize(x.Title))).ToList()
            : [];
        var untouchableIds = untouchable.Select(x => x.PlaylistItemId).ToHashSet(StringComparer.Ordinal);
        var dedupable = items.Where(x => !untouchableIds.Contains(x.PlaylistItemId));

        // Mantenemos el primero de cada grupo (menor Position), eliminamos el resto.
        IEnumerable<IGrouping<string, PlaylistItemDto>> groups = req.Strategy == "normalizedTitle"
            ? dedupable.GroupBy(x => Normalize(x.Title))
            : dedupable.Where(x => !string.IsNullOrEmpty(x.VideoId)).GroupBy(x => x.VideoId);

        var toKeep = new List<PlaylistItemDto>(untouchable);
        int removed = 0;

        foreach (var g in groups)
        {
            var ordered = g.OrderBy(x => x.Position).ToList();
            if (ordered.Count == 0) continue;
            toKeep.Add(ordered[0]);
            removed += ordered.Count - 1;
        }

        // Reordenar por Position para que la lista se vea coherente.
        toKeep = toKeep.OrderBy(x => x.Position).ToList();

        itemsCache.Save(userKey, req.PlaylistId, toKeep);
        touchStore.Touch(req.PlaylistId);
        log.Add("RemoveDuplicates", $"playlist={req.PlaylistId} strategy={req.Strategy} removed={removed} kept={toKeep.Count} (local)");
        return new RemoveDuplicatesResultDto(req.PlaylistId, removed, toKeep.Count);
    }

    // ── Normalización de títulos ──
    private static readonly Regex NonWord = new(@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] NoiseTokens =
    [
        "official", "video", "videoclip", "audio", "lyrics", "letra", "hd", "hq",
        "remastered", "remaster", "mv", "feat", "ft", "featuring", "cover"
    ];

    /// <summary>«Mismo título» tolerante: sin paréntesis, diacríticos, signos ni ruido tipo "official video".</summary>
    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        // quitar paréntesis/brackets enteros
        var t = Regex.Replace(title, @"[\(\[].*?[\)\]]", " ");
        t = t.ToLower(CultureInfo.InvariantCulture);
        // quitar diacríticos
        var formD = t.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in formD)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        t = sb.ToString().Normalize(NormalizationForm.FormC);
        t = NonWord.Replace(t, " ");
        var tokens = MultiSpace.Split(t).Where(x => x.Length > 1 && !NoiseTokens.Contains(x));
        return string.Join(" ", tokens).Trim();
    }
}
