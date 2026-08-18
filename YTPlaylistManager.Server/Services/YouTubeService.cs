using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.YouTube.v3.Data;
using YTPlaylistManager.Server.Domain.Entities;
using YTPlaylistManager.Server.Domain.Exceptions;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

public class YouTubeService : IYouTubeService
{
    private readonly IConfiguration _cfg;
    private readonly GoogleTokenStore _tokenStore;
    private readonly OperationLog _log;
    private readonly PlaylistCacheStore _cacheStore;
    private readonly PlaylistItemsCacheStore _itemsCache;
    private readonly ArchivedPlaylistsStore _archivedStore;
    private readonly MergeReviewStore _reviewStore;
    private readonly PendingUploadStore _pendingUploads;
    private readonly PendingSongMoveStore _songMoves;
    private readonly QuotaTracker _quota;
    private readonly ActivityBroadcaster _activity;
    private readonly PlaylistTouchStore _touchStore;
    private readonly GoogleSessionValidator _session;
    private readonly ILogger<YouTubeService> _logger;

    public YouTubeService(
        IConfiguration cfg,
        GoogleTokenStore tokenStore,
        OperationLog log,
        PlaylistCacheStore cacheStore,
        PlaylistItemsCacheStore itemsCache,
        ArchivedPlaylistsStore archivedStore,
        MergeReviewStore reviewStore,
        PendingUploadStore pendingUploads,
        PendingSongMoveStore songMoves,
        QuotaTracker quota,
        ActivityBroadcaster activity,
        PlaylistTouchStore touchStore,
        GoogleSessionValidator session,
        ILogger<YouTubeService> logger)
    {
        _cfg = cfg;
        _tokenStore = tokenStore;
        _log = log;
        _cacheStore = cacheStore;
        _itemsCache = itemsCache;
        _archivedStore = archivedStore;
        _reviewStore = reviewStore;
        _pendingUploads = pendingUploads;
        _songMoves = songMoves;
        _quota = quota;
        _activity = activity;
        _touchStore = touchStore;
        _session = session;
        _logger = logger;
    }

    /// <summary>Clave estable por cuenta. No gasta cuota.</summary>
    private string CurrentUserKey()
    {
        var t = _tokenStore.Load();
        // Preferencia: AccountId (channel id, estable entre logins). Fallback legado:
        // refresh token — rota si Google emite uno nuevo y fragmenta los datos.
        if (!string.IsNullOrEmpty(t?.AccountId)) return UserKeys.FromSeed(t.AccountId);
        return UserKeys.FromSeed(t?.RefreshToken ?? "anon");
    }

    /// <summary>
    /// Ids de playlists vigentes según la caché de la lista (0 cuota). Devuelve null si
    /// la caché aún no existe: en ese caso NO se puede afirmar que una lista falte, así
    /// que las validaciones deben dejar pasar en vez de bloquear por falta de datos.
    /// </summary>
    private HashSet<string>? KnownPlaylistIds()
    {
        var cache = _cacheStore.Load();
        return cache is null ? null : cache.Playlists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True solo si sabemos con certeza que la playlist ya no existe.</summary>
    private bool IsKnownMissing(string playlistId)
    {
        // Las listas del canal (Favoritos, Ver más tarde, Me gusta, Mezcla) nunca aparecen
        // en playlists.list?mine=true, así que faltar de la caché no prueba nada sobre
        // ellas: existen, solo que por otra vía.
        if (IsSpecialPlaylist(playlistId)) return false;
        var known = KnownPlaylistIds();
        return known is not null && !known.Contains(playlistId);
    }

    // Playlists especiales de YouTube (FL=Favoritos, WL=Ver más tarde, LL=Me gusta, RD=Mezcla)
    // que la API no permite borrar. Si una lista origen tiene este prefijo, la omitimos en lugar
    // de dejar el pending bloqueado para siempre.
    private static bool IsSpecialPlaylist(string id) =>
        id.StartsWith("FL", StringComparison.Ordinal) ||
        id.StartsWith("WL", StringComparison.Ordinal) ||
        id.StartsWith("LL", StringComparison.Ordinal) ||
        id.StartsWith("RD", StringComparison.Ordinal);

    /// <summary>Anota la última modificación local registrada (PlaylistTouchStore).</summary>
    private List<PlaylistDto> AnnotateTouched(List<PlaylistDto> source)
    {
        var touched = _touchStore.LoadAll();
        if (touched.Count == 0) return source;
        return source
            .Select(p => touched.TryGetValue(p.Id, out var at) ? p with { LastModifiedUtc = at } : p)
            .ToList();
    }

    /// <summary>Marca las playlists que son origen de un cambio pendiente (en cola de unir).</summary>
    private List<PlaylistDto> AnnotateQueued(List<PlaylistDto> source)
    {
        var pending = _pendingUploads.LoadForUser(CurrentUserKey());
        if (pending.Count == 0) return source;

        var queued = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in pending)
            foreach (var s in p.Sources)
                queued[s.Id] = p.TargetPlaylistTitle;

        return source.Select(p => queued.TryGetValue(p.Id, out var t)
            ? p with { QueuedForMerge = true, QueuedIntoTitle = t }
            : p).ToList();
    }

    /// <summary>Marca las playlists archivadas localmente y las quita si no se piden.</summary>
    private List<PlaylistDto> AnnotateArchived(List<PlaylistDto> source, bool includeArchived)
    {
        var archived = _archivedStore.LoadAll();
        if (archived.Count == 0) return source;

        var archivedById = archived.ToDictionary(a => a.Id);
        var annotated = source.Select(p => archivedById.TryGetValue(p.Id, out var a)
            ? p with { IsArchived = true, ArchivedIntoPlaylistId = a.MergedIntoPlaylistId, ArchivedIntoPlaylistTitle = a.MergedIntoPlaylistTitle }
            : p).ToList();

        return includeArchived ? annotated : annotated.Where(p => !p.IsArchived).ToList();
    }

    private Google.Apis.YouTube.v3.YouTubeService BuildClient()
    {
        var token = _tokenStore.Load()
            ?? throw new NotAuthenticatedException("No hay sesión Google activa. Visita /api/auth/login primero.");

        // Flujo con DataStore: el access token renovado se persiste en vez de perderse
        // al terminar la petición.
        var flow = _session.CreateFlow();

        var tokenResponse = new TokenResponse
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresInSeconds = (long)Math.Max(0, (token.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds),
            IssuedUtc = DateTime.UtcNow.AddSeconds(-1),
            Scope = token.Scope
        };

        var credential = new UserCredential(flow, "me", tokenResponse);

        return new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "YTPlaylistManager"
        });
    }

    public async Task<List<PlaylistDto>> GetMyPlaylistsAsync(CancellationToken ct = default, bool forceRefresh = false, bool includeArchived = false)
    {
        var userKey = CurrentUserKey();
        var cache = _cacheStore.Load();

        // Caché de la misma cuenta y sin pedir refrescar → servimos del archivo (0 cuota).
        if (!forceRefresh && cache is not null && cache.UserKey == userKey)
            return AnnotateTouched(AnnotateQueued(AnnotateArchived(cache.Playlists, includeArchived)));

        try
        {
            var yt = BuildClient();
            var result = new List<PlaylistDto>();
            string? pageToken = null;

            do
            {
                var req = yt.Playlists.List("snippet,contentDetails,status");
                req.Mine = true;
                req.MaxResults = 50;
                req.PageToken = pageToken;
                var resp = await req.ExecuteAsync(ct);
                _quota.Add(1);

                foreach (var p in resp.Items)
                {
                    result.Add(new PlaylistDto(
                        p.Id,
                        p.Snippet.Title,
                        p.Snippet.Description,
                        (int)(p.ContentDetails?.ItemCount ?? 0),
                        p.Snippet.Thumbnails?.Default__?.Url,
                        p.Status?.PrivacyStatus
                    ));
                }

                pageToken = resp.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            var ordered = result.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
            _cacheStore.Save(new PlaylistCache { UserKey = userKey, CachedAtUtc = DateTime.UtcNow, Playlists = ordered });
            return AnnotateTouched(AnnotateQueued(AnnotateArchived(ordered, includeArchived)));
        }
        catch (Google.GoogleApiException ex)
        {
            // API falló (cuota/red). Si hay caché de esta misma cuenta, la usamos en vez de romper.
            if (cache is not null && cache.UserKey == userKey)
            {
                _logger.LogWarning(ex, "Fallo al listar playlists ({Status}); usando caché.", ex.HttpStatusCode);
                return AnnotateTouched(AnnotateQueued(AnnotateArchived(cache.Playlists, includeArchived)));
            }
            throw;
        }
    }

    public async Task<List<PlaylistItemDto>> GetPlaylistItemsAsync(string playlistId, CancellationToken ct = default, bool forceRefresh = false)
    {
        var userKey = CurrentUserKey();

        // Caché de items: si ya leímos esta playlist y no se pide refrescar → 0 cuota.
        if (!forceRefresh)
        {
            var cached = _itemsCache.Load(userKey, playlistId);
            if (cached is not null) return cached;
        }

        try
        {
            var items = await FetchItemsAsync(BuildClient(), playlistId, ct);
            _itemsCache.Save(userKey, playlistId, items);  // guardar para no re-leer
            return items;
        }
        catch (Google.GoogleApiException ex)
        {
            // API falló (cuota/red). Si hay caché de esta playlist, la usamos en vez de romper.
            var cached = _itemsCache.Load(userKey, playlistId);
            if (cached is not null)
            {
                _logger.LogWarning(ex, "Items de {Playlist}: API falló ({Status}); usando caché.", playlistId, ex.HttpStatusCode);
                return cached;
            }
            throw;
        }
    }

    private async Task<List<PlaylistItemDto>> FetchItemsAsync(
        Google.Apis.YouTube.v3.YouTubeService yt, string playlistId, CancellationToken ct)
    {
        var result = new List<PlaylistItemDto>();
        string? pageToken = null;

        do
        {
            var req = yt.PlaylistItems.List("snippet,contentDetails");
            req.PlaylistId = playlistId;
            req.MaxResults = 50;
            req.PageToken = pageToken;
            var resp = await req.ExecuteAsync(ct);
            _quota.Add(1);

            foreach (var it in resp.Items)
            {
                // Algunos items pueden ser videos eliminados; los conservamos pero con título de placeholder.
                result.Add(new PlaylistItemDto(
                    it.Id,
                    it.ContentDetails?.VideoId ?? it.Snippet.ResourceId?.VideoId ?? "",
                    it.Snippet.Title ?? "(sin título)",
                    it.Snippet.VideoOwnerChannelTitle ?? it.Snippet.ChannelTitle,
                    (int)(it.Snippet.Position ?? 0),
                    it.Snippet.Thumbnails?.Default__?.Url
                ));
            }

            pageToken = resp.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return result.OrderBy(x => x.Position).ToList();
    }

    public async Task<DuplicateReportDto> FindDuplicatesAsync(string playlistId, CancellationToken ct = default)
    {
        // El título es decorativo: si su fetch falla (cuota, 404, red) se usa el id
        // como fallback en vez de tumbar toda la detección de duplicados.
        var playlistTitle = playlistId;
        try
        {
            var yt = BuildClient();
            var playlistReq = yt.Playlists.List("snippet");
            playlistReq.Id = playlistId;
            var pResp = await playlistReq.ExecuteAsync(ct);
            _quota.Add(1);
            playlistTitle = pResp.Items.FirstOrDefault()?.Snippet.Title ?? playlistId;
        }
        catch (Google.GoogleApiException ex)
        {
            if (QuotaTracker.IsQuotaError(ex)) _quota.MarkExhausted();
            _logger.LogWarning(ex, "No se pudo leer el título de {Playlist}; se usa el id.", playlistId);
        }

        // Detección precisa: re-leemos los items DESDE YouTube (no de la caché, que puede
        // estar desincronizada por remociones locales no subidas). Esto refresca la caché.
        // Si ESTO falla, la excepción sube al middleware global (403 cuota / 404 / etc.).
        var items = await GetPlaylistItemsAsync(playlistId, ct, forceRefresh: true);

        var groups = new List<DuplicateGroupDto>();

        // Por videoId
        foreach (var g in items
                     .Where(x => !string.IsNullOrEmpty(x.VideoId) && !VideoAvailability.IsUnavailable(x.Title))
                     .GroupBy(x => x.VideoId)
                     .Where(g => g.Count() > 1))
        {
            groups.Add(new DuplicateGroupDto(g.Key, "videoId", g.OrderBy(x => x.Position).ToList()));
        }

        // Por título normalizado (capta "misma canción con distinto video")
        var alreadyFlagged = new HashSet<string>(groups.SelectMany(g => g.Items).Select(i => i.PlaylistItemId));
        foreach (var g in items
                     .Where(x => !VideoAvailability.IsUnavailable(x.Title))
                     .GroupBy(x => Normalize(x.Title))
                     .Where(g => g.Count() > 1 && !string.IsNullOrWhiteSpace(g.Key)))
        {
            var dupes = g.Where(x => !alreadyFlagged.Contains(x.PlaylistItemId)).ToList();
            if (dupes.Count > 1)
                groups.Add(new DuplicateGroupDto(g.Key, "normalizedTitle", dupes.OrderBy(x => x.Position).ToList()));
        }

        var dupCount = groups.Sum(g => g.Items.Count - 1);

        return new DuplicateReportDto(playlistId, playlistTitle, items.Count, dupCount, groups);
    }

    public async Task<CrossDuplicateReportDto> FindCrossDuplicatesAsync(CancellationToken ct = default, bool forceRefresh = false)
    {
        var playlists = await GetMyPlaylistsAsync(ct);

        var map = new Dictionary<string, (string Title, Dictionary<string, string> Playlists)>();
        int scanned = 0, failed = 0;
        Google.GoogleApiException? lastError = null;

        foreach (var pl in playlists)
        {
            List<PlaylistItemDto> items;
            try
            {
                items = await GetPlaylistItemsAsync(pl.Id, ct, forceRefresh);
                scanned++;
            }
            catch (Google.GoogleApiException ex)
            {
                failed++;
                lastError = ex;
                _logger.LogWarning(ex, "No se pudo leer la playlist {Playlist} ({Status}); se omite.", pl.Id, ex.HttpStatusCode);
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

    public async Task<RemoveDuplicatesResultDto> RemoveDuplicatesAsync(RemoveDuplicatesRequest req, CancellationToken ct = default)
    {
        var userKey = CurrentUserKey();

        // LOCAL: operamos sobre la caché. Si la playlist no está cacheada no podemos
        // deduplicar (no sabemos qué hay). El usuario debe abrir la playlist primero
        // (lo que la cachea) y volver a intentar.
        var items = _itemsCache.Load(userKey, req.PlaylistId);
        if (items is null)
        {
            throw new InvalidOperationException(
                "La playlist no está en caché. Abrila una vez desde la app para que se cargue y volvé a intentar.");
        }

        // Mantenemos el primero de cada grupo (menor Position), eliminamos el resto.
        IEnumerable<IGrouping<string, PlaylistItemDto>> groups = req.Strategy == "normalizedTitle"
            ? items.GroupBy(x => Normalize(x.Title))
            : items.Where(x => !string.IsNullOrEmpty(x.VideoId)).GroupBy(x => x.VideoId);

        var keepIds = new HashSet<string>();
        var toKeep = new List<PlaylistItemDto>();
        int removed = 0;
        int kept = 0;

        foreach (var g in groups)
        {
            var ordered = g.OrderBy(x => x.Position).ToList();
            if (ordered.Count == 0) continue;
            var first = ordered[0];
            toKeep.Add(first);
            keepIds.Add(first.PlaylistItemId);
            kept++;
            removed += ordered.Count - 1;
        }

        // Reordenar por Position para que la lista se vea coherente.
        toKeep = toKeep.OrderBy(x => x.Position).ToList();

        _itemsCache.Save(userKey, req.PlaylistId, toKeep);
        _touchStore.Touch(req.PlaylistId);
        _log.Add("RemoveDuplicates", $"playlist={req.PlaylistId} strategy={req.Strategy} removed={removed} kept={kept} (local)");
        return new RemoveDuplicatesResultDto(req.PlaylistId, removed, kept);
    }

    private sealed class PreviewAccum(string title, string? channelTitle, string? thumbnailUrl)
    {
        public string Title { get; } = title;
        public string? ChannelTitle { get; } = channelTitle;
        public string? ThumbnailUrl { get; } = thumbnailUrl;
        public HashSet<string> From { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Vista previa del merge (solo caché → 0 cuota): agrupa por videoId las canciones
    /// que faltan en el target, marcando en cada una las listas origen donde aparece
    /// (una sola fila por canción, no una por aparición).
    /// </summary>
    public MergePreviewDto PreviewMerge(MergePreviewRequest req)
    {
        var userKey = CurrentUserKey();
        var cache = _cacheStore.Load();
        var targetId = req.TargetPlaylistId;
        var targetTitle = cache?.Playlists?.FirstOrDefault(p => p.Id == targetId)?.Title ?? targetId;
        var warnings = new List<string>();

        var targetItems = _itemsCache.Load(userKey, targetId);
        if (targetItems is null)
            warnings.Add($"La lista destino «{targetTitle}» no está cargada; abrila o usá «Actualizar» para un cálculo exacto.");

        var existing = new HashSet<string>(
            (targetItems ?? new List<PlaylistItemDto>())
                .Where(i => !string.IsNullOrEmpty(i.VideoId)).Select(i => i.VideoId),
            StringComparer.Ordinal);

        var byVideo = new Dictionary<string, PreviewAccum>(StringComparer.Ordinal);
        int alreadyPresent = 0;

        foreach (var src in req.SourcePlaylistIds.Distinct())
        {
            if (src == targetId) continue;
            var srcTitle = cache?.Playlists?.FirstOrDefault(p => p.Id == src)?.Title ?? src;
            var items = _itemsCache.Load(userKey, src);
            if (items is null)
            {
                warnings.Add($"La lista «{srcTitle}» no está cargada; abrila para incluirla en la vista previa.");
                continue;
            }

            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.VideoId)) continue;
                if (existing.Contains(it.VideoId)) { alreadyPresent++; continue; }

                if (!byVideo.TryGetValue(it.VideoId, out var acc))
                {
                    acc = new PreviewAccum(it.Title, it.ChannelTitle, it.ThumbnailUrl);
                    byVideo[it.VideoId] = acc;
                }
                acc.From.Add(srcTitle);
            }
        }

        var toAdd = byVideo
            .Select(kv => new MergePreviewSongDto(
                kv.Key, kv.Value.Title, kv.Value.ChannelTitle ?? "", kv.Value.ThumbnailUrl,
                kv.Value.From.ToList()))
            .OrderByDescending(i => i.FromPlaylists.Count)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MergePreviewDto(
            targetId, targetTitle, toAdd.Count, alreadyPresent, toAdd.Count * 50, toAdd, warnings);
    }

    public Task<MergePlaylistsResultDto> MergePlaylistsAsync(MergePlaylistsRequest req, CancellationToken ct = default)
    {
        var userKey = CurrentUserKey();

        if (string.IsNullOrEmpty(req.TargetPlaylistId))
            throw new ArgumentException("TargetPlaylistId es requerido (merge SIEMPRE hacia una playlist existente).");
        if (req.SourcePlaylistIds is null || req.SourcePlaylistIds.Count == 0)
            throw new ArgumentException("SourcePlaylistIds no puede estar vacío.");
        if (IsSpecialPlaylist(req.TargetPlaylistId))
            throw new ArgumentException(
                "YouTube no permite modificar sus listas automáticas (Favoritos, Ver más tarde, " +
                "Me gusta) desde la API. Elegí otra lista destino.");

        var targetId = req.TargetPlaylistId;
        var cache = _cacheStore.Load();
        var targetTitle = cache?.Playlists?.FirstOrDefault(p => p.Id == targetId)?.Title ?? targetId;

        // Unión EN LOCAL (0 cuota): trabajamos sobre la caché de items.
        var targetItems = _itemsCache.Load(userKey, targetId) ?? new List<PlaylistItemDto>();
        var existing = new HashSet<string>(
            targetItems.Where(i => !string.IsNullOrEmpty(i.VideoId)).Select(i => i.VideoId),
            StringComparer.Ordinal);

        // Agrupar por videoId las canciones que faltan (una fila por canción + listas origen).
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);
        var byVideo = new Dictionary<string, PreviewAccum>(StringComparer.Ordinal);
        var sourcesUsed = new List<PendingSource>();
        int skipped = 0;
        int nextPosition = targetItems.Count == 0 ? 0 : targetItems.Max(i => i.Position) + 1;

        foreach (var src in req.SourcePlaylistIds.Distinct())
        {
            if (src == targetId) continue;
            var srcTitle = cache?.Playlists?.FirstOrDefault(p => p.Id == src)?.Title ?? src;
            var items = _itemsCache.Load(userKey, src);
            if (items is null) continue;   // lista origen no cargada → se omite (el preview avisa)
            sourcesUsed.Add(new PendingSource { Id = src, Title = srcTitle });

            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.VideoId)) continue;
                if (existing.Contains(it.VideoId)) { skipped++; continue; }   // ya está en el target

                if (!seen.Add(it.VideoId))
                {
                    // ya contada como nueva desde otra lista origen → solo sumamos la procedencia
                    if (byVideo.TryGetValue(it.VideoId, out var prev)) prev.From.Add(srcTitle);
                    continue;
                }

                var acc = new PreviewAccum(it.Title, it.ChannelTitle, it.ThumbnailUrl);
                acc.From.Add(srcTitle);
                byVideo[it.VideoId] = acc;
            }
        }

        // Materializar la unión EN LOCAL: items sintéticos en la caché del target.
        var pendingItems = new List<PendingUploadItem>();
        var newCacheItems = new List<PlaylistItemDto>();
        foreach (var (videoId, acc) in byVideo)
        {
            var localId = $"pending-{Guid.NewGuid():N}";
            pendingItems.Add(new PendingUploadItem
            {
                LocalItemId = localId,
                VideoId = videoId,
                Title = acc.Title,
                ChannelTitle = acc.ChannelTitle,
                ThumbnailUrl = acc.ThumbnailUrl,
                FromPlaylists = acc.From.ToList(),
            });
            newCacheItems.Add(new PlaylistItemDto(
                PlaylistItemId: localId,
                VideoId: videoId,
                Title: acc.Title,
                ChannelTitle: acc.ChannelTitle,
                Position: nextPosition++,
                ThumbnailUrl: acc.ThumbnailUrl));
        }

        if (newCacheItems.Count > 0)
            _itemsCache.Save(userKey, targetId, targetItems.Concat(newCacheItems).ToList());

        int added = pendingItems.Count;
        string? pendingId = null;
        // Crear el pendiente aunque no haya canciones nuevas: si hay listas origen,
        // unir = borrarlas y conservar la destino (quedan en cola para borrar al subir).
        if (added > 0 || sourcesUsed.Count > 0)
        {
            pendingId = Guid.NewGuid().ToString("N")[..12];
            _pendingUploads.Add(new PendingUpload
            {
                Id = pendingId,
                UserKey = userKey,
                TargetPlaylistId = targetId,
                TargetPlaylistTitle = targetTitle,
                Items = pendingItems,
                Sources = sourcesUsed,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        if (added > 0 || sourcesUsed.Count > 0)
            _touchStore.Touch(targetId);
        _log.Add("Merge(local)", $"sources={string.Join(",", req.SourcePlaylistIds)} target={targetId} staged={added} skipped={skipped} pendingId={pendingId}");

        return Task.FromResult(new MergePlaylistsResultDto(
            targetId, targetTitle, added, skipped, 0, pendingId, 0, false));
    }

    /// <summary>Sube a YouTube de verdad las canciones de un cambio pendiente (50u c/u).
    /// <paramref name="limit"/> limita el número de canciones a subir en esta llamada; sin valor sube todo.</summary>
    public async Task<UploadResultDto> UploadPendingAsync(string id, int? limit = null, CancellationToken ct = default)
    {
        var userKey = CurrentUserKey();
        var plan = _pendingUploads.Get(id)
            ?? throw new ArgumentException("El cambio pendiente no existe (quizás ya se subió).");
        if (plan.UserKey != userKey)
            throw new NotAuthenticatedException("Ese cambio pendiente es de otra cuenta.");

        // Las listas automáticas de YouTube (FL/WL/LL/RD) no admiten escritura por API desde
        // 2016: el insert responde 404 aunque la lista exista y se vea en la web.
        if (IsSpecialPlaylist(plan.TargetPlaylistId))
            return new UploadResultDto(id, plan.TargetPlaylistId, plan.TargetPlaylistTitle,
                0, 0, false, plan.Items.Count, 0, plan.Sources.Count, TargetMissing: false, TargetLocked: true);

        // Validación previa (0 cuota): si la lista destino ya no existe, cortar acá —
        // sin gastar unidades ni tocar la caché ni las listas origen. No es un error de
        // la petición sino un estado del mundo, así que se informa en el resultado.
        if (IsKnownMissing(plan.TargetPlaylistId))
            return new UploadResultDto(id, plan.TargetPlaylistId, plan.TargetPlaylistTitle,
                0, 0, false, plan.Items.Count, 0, plan.Sources.Count, TargetMissing: true);

        var yt = BuildClient();
        var targetId = plan.TargetPlaylistId;
        int uploaded = 0, failed = 0;
        bool paused = false;
        bool targetMissing = false;
        var remaining = new List<PendingUploadItem>();
        var realIdByLocal = new Dictionary<string, string>(StringComparer.Ordinal);  // localId -> id real de YouTube
        var failedLocalIds = new HashSet<string>(StringComparer.Ordinal);
        int processed = 0;

        foreach (var item in plan.Items)
        {
            if (paused || targetMissing || (limit.HasValue && processed >= limit.Value)) { remaining.Add(item); continue; }
            try
            {
                var inserted = await yt.PlaylistItems.Insert(new PlaylistItem
                {
                    Snippet = new PlaylistItemSnippet
                    {
                        PlaylistId = targetId,
                        ResourceId = new ResourceId { Kind = "youtube#video", VideoId = item.VideoId },
                    },
                }, "snippet").ExecuteAsync(ct);
                _quota.Add(50);
                _activity.Publish(new ActivityEvent("insert", item.Title, plan.TargetPlaylistTitle, item.VideoId, DateTime.UtcNow));
                realIdByLocal[item.LocalItemId] = inserted.Id;
                uploaded++;
                processed++;
            }
            catch (Google.GoogleApiException ex) when (QuotaTracker.IsQuotaError(ex))
            {
                _quota.MarkExhausted();
                paused = true;
                remaining.Add(item);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Target inexistente (borrado después de encolar): abortar TODO el plan en
                // vez de iterar fallando item por item; el resto queda pendiente y las
                // fuentes NO se tocan.
                _logger.LogWarning(ex, "Target {Target} no existe; se aborta la subida del pendiente {Id}.", targetId, id);
                targetMissing = true;
                remaining.Add(item);
            }
            catch (Google.GoogleApiException ex)
            {
                _logger.LogWarning(ex, "No se pudo subir {Video} a {Target}.", item.VideoId, targetId);
                failedLocalIds.Add(item.LocalItemId);
                failed++;
                processed++;
            }
        }

        // Actualizar la caché del target EN EL LUGAR (sin leer de YouTube): cambiar los ids
        // sintéticos por los reales del insert y quitar las que fallaron.
        if (realIdByLocal.Count > 0 || failedLocalIds.Count > 0)
        {
            var cached = _itemsCache.Load(userKey, targetId);
            if (cached is not null)
            {
                var updated = cached
                    .Where(i => !failedLocalIds.Contains(i.PlaylistItemId))
                    .Select(i => realIdByLocal.TryGetValue(i.PlaylistItemId, out var realId)
                        ? i with { PlaylistItemId = realId }
                        : i)
                    .ToList();
                _itemsCache.Save(userKey, targetId, updated);
            }
        }

        // Borrar las listas origen de YouTube SOLO cuando TODAS las canciones se subieron
        // de verdad: sin restantes, sin fallos y con el target vivo. Un plan con fallos
        // queda pendiente (descartable a mano) — nunca se borra una fuente sin haber
        // copiado su contenido.
        int deletedSources = 0;
        var remainingSources = plan.Sources;
        if (remaining.Count == 0 && failed == 0 && !targetMissing && plan.Sources.Count > 0)
        {
            var stillPending = new List<PendingSource>();
            var deletedIds = new List<string>();
            var archivedEntries = new List<ArchivedPlaylistEntry>();
            foreach (var s in plan.Sources)
            {
                if (paused) { stillPending.Add(s); continue; }
                // Playlists especiales (FL/WL/LL/RD): la API de YouTube no permite borrarlas.
                // Las consideramos "completadas" para que el pending no quede bloqueado.
                if (IsSpecialPlaylist(s.Id))
                {
                    _logger.LogWarning("Lista origen {Id} ({Title}) es especial de YouTube y no se puede borrar via API; se omite.", s.Id, s.Title);
                    _itemsCache.Invalidate(userKey, s.Id);
                    continue;
                }
                var songsCount = _itemsCache.Load(userKey, s.Id)?.Count ?? 0;
                try
                {
                    await yt.Playlists.Delete(s.Id).ExecuteAsync(ct);
                    _quota.Add(50);
                    _activity.Publish(new ActivityEvent("delete-list", s.Title, "", "", DateTime.UtcNow));
                    deletedIds.Add(s.Id);
                    archivedEntries.Add(new ArchivedPlaylistEntry
                    {
                        Id = s.Id,
                        Title = s.Title,
                        ArchivedAtUtc = DateTime.UtcNow,
                        MergedIntoPlaylistId = targetId,
                        MergedIntoPlaylistTitle = plan.TargetPlaylistTitle,
                        SongsCount = songsCount,
                    });
                    _itemsCache.Invalidate(userKey, s.Id);
                }
                catch (Google.GoogleApiException ex) when (QuotaTracker.IsQuotaError(ex))
                {
                    _quota.MarkExhausted();
                    paused = true;
                    stillPending.Add(s);
                }
                catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    deletedIds.Add(s.Id);   // ya no existía → la damos por borrada
                    archivedEntries.Add(new ArchivedPlaylistEntry
                    {
                        Id = s.Id,
                        Title = s.Title,
                        ArchivedAtUtc = DateTime.UtcNow,
                        MergedIntoPlaylistId = targetId,
                        MergedIntoPlaylistTitle = plan.TargetPlaylistTitle,
                        SongsCount = songsCount,
                    });
                    _itemsCache.Invalidate(userKey, s.Id);
                }
                catch (Google.GoogleApiException ex)
                {
                    _logger.LogWarning(ex, "No se pudo borrar la lista origen {Source}.", s.Id);
                    stillPending.Add(s);
                }
            }
            deletedSources = deletedIds.Count;
            remainingSources = stillPending;
            if (archivedEntries.Count > 0) _archivedStore.Add(archivedEntries);
            RemoveFromPlaylistListCache(deletedIds);
        }

        if (remaining.Count == 0 && remainingSources.Count == 0)
        {
            _pendingUploads.Remove(id);
        }
        else
        {
            plan.Items = remaining;
            plan.Sources = remainingSources;
            _pendingUploads.Replace(plan);
        }

        _log.Add("Upload", $"pending={id} target={targetId} uploaded={uploaded} failed={failed} deletedSources={deletedSources} paused={paused} remItems={remaining.Count} remSources={remainingSources.Count} targetMissing={targetMissing}");

        // YouTube respondió 404: la lista destino ya no existe. Se saca de la caché de
        // listas para que el próximo listado de pendientes ya venga con TargetMissing y
        // la UI oculte el botón de subir, en vez de chocar contra el mismo 404 cada vez.
        if (targetMissing) RemoveFromPlaylistListCache([targetId]);

        return new UploadResultDto(id, targetId, plan.TargetPlaylistTitle, uploaded, failed, paused,
            remaining.Count, deletedSources, remainingSources.Count, targetMissing);
    }

    /// <summary>Quita playlists de la caché de la lista (tras borrarlas en YouTube).</summary>
    private void RemoveFromPlaylistListCache(IEnumerable<string> ids)
    {
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        if (idSet.Count == 0) return;
        var cache = _cacheStore.Load();
        if (cache is null) return;
        var filtered = cache.Playlists.Where(p => !idSet.Contains(p.Id)).ToList();
        if (filtered.Count != cache.Playlists.Count)
            _cacheStore.Save(new PlaylistCache { UserKey = cache.UserKey, CachedAtUtc = cache.CachedAtUtc, Playlists = filtered });
    }

    public List<PendingUploadDto> GetPendingUploads()
    {
        var userKey = CurrentUserKey();
        var known = KnownPlaylistIds();
        return [.. _pendingUploads.LoadForUser(userKey)
                .Select(p => new PendingUploadDto(
                    p.Id, p.TargetPlaylistId, p.TargetPlaylistTitle,
                    p.Items.Count, (p.Items.Count + p.Sources.Count) * 50, p.CreatedAtUtc,
                [.. p.Items.Select(i => new PendingUploadItemDto(i.VideoId, i.Title, i.ChannelTitle ?? "", i.ThumbnailUrl, i.FromPlaylists))],
                [.. p.Sources.Select(s => s.Title)],
                    !IsSpecialPlaylist(p.TargetPlaylistId) && known is not null && !known.Contains(p.TargetPlaylistId),
                    IsSpecialPlaylist(p.TargetPlaylistId)))
               ];
    }

    /// <summary>Descarta un cambio pendiente y revierte la unión local del target.</summary>
    public void DiscardPending(string id)
    {
        var userKey = CurrentUserKey();
        var plan = _pendingUploads.Get(id);
        if (plan is null) return;
        if (plan.UserKey != userKey && plan.Items.Count > 0)
            throw new NotAuthenticatedException("Ese cambio pendiente es de otra cuenta.");

        var localIds = plan.Items.Select(i => i.LocalItemId).ToHashSet(StringComparer.Ordinal);
        var targetItems = _itemsCache.Load(userKey, plan.TargetPlaylistId);
        if (targetItems is not null)
            _itemsCache.Save(userKey, plan.TargetPlaylistId,
                targetItems.Where(i => !localIds.Contains(i.PlaylistItemId)).ToList());

        _pendingUploads.Remove(id);
        _log.Add("DiscardPending", $"pending={id} target={plan.TargetPlaylistId} reverted={plan.Items.Count}");
    }

    // ── Asignar una canción a playlists (staged: local → pendiente → subir) ──

    /// <summary>Playlists de la cuenta que contienen el videoId, con su playlistItemId (solo caché).</summary>
    private Dictionary<string, (string Title, string ItemId)> CurrentSongLocations(string userKey, string videoId)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var cache = _cacheStore.Load();
        if (cache?.Playlists is null) return map;
        foreach (var pl in cache.Playlists)
        {
            var items = _itemsCache.Load(userKey, pl.Id);
            var hit = items?.FirstOrDefault(i => i.VideoId == videoId);
            if (hit is not null) map[pl.Id] = (pl.Title, hit.PlaylistItemId);
        }
        return map;
    }

    private PendingSongMoveDto ToDto(PendingSongMove m)
    {
        // Listas involucradas que ya no existen: se informan para que el panel lo avise;
        // al subir se omiten en vez de contarse como fallo.
        var known = KnownPlaylistIds();
        var missing = known is null
            ? []
            : m.AddTo.Select(a => (a.PlaylistId, a.PlaylistTitle))
                .Concat(m.RemoveFrom.Select(r => (r.PlaylistId, r.PlaylistTitle)))
                .Where(x => !known.Contains(x.Item1))
                .Select(x => x.Item2)
                .Distinct()
                .ToList();

        return new PendingSongMoveDto(
            m.Id, m.VideoId, m.Title, m.ThumbnailUrl,
            m.AddTo.Select(a => a.PlaylistTitle).ToList(),
            m.RemoveFrom.Select(r => r.PlaylistTitle).ToList(),
            (m.AddTo.Count + m.RemoveFrom.Count) * 50,
            m.CreatedAtUtc,
            missing);
    }

    /// <summary>Aplica en local la reasignación (agregar/quitar) y la deja pendiente de subir.</summary>
    public PendingSongMoveDto? StageSongAssignment(AssignSongRequest req)
    {
        if (string.IsNullOrEmpty(req.VideoId)) throw new ArgumentException("VideoId requerido.");
        var userKey = CurrentUserKey();
        var cache = _cacheStore.Load();
        var titleById = cache?.Playlists?.ToDictionary(p => p.Id, p => p.Title) ?? new();

        var current = CurrentSongLocations(userKey, req.VideoId);
        var desired = new HashSet<string>(req.DesiredPlaylistIds ?? [], StringComparer.Ordinal);

        var addTo = new List<SongMoveTarget>();
        var removeFrom = new List<SongMoveRemoval>();

        foreach (var pid in desired)
        {
            if (current.ContainsKey(pid)) continue;
            addTo.Add(new SongMoveTarget
            {
                PlaylistId = pid,
                PlaylistTitle = titleById.GetValueOrDefault(pid, pid),
                LocalItemId = $"pending-{Guid.NewGuid():N}",
            });
        }
        foreach (var (pid, info) in current)
        {
            if (desired.Contains(pid)) continue;
            removeFrom.Add(new SongMoveRemoval { PlaylistId = pid, PlaylistTitle = info.Title, PlaylistItemId = info.ItemId });
        }

        if (addTo.Count == 0 && removeFrom.Count == 0) return null;

        // Aplicar en LOCAL: agregar items sintéticos / quitar de la caché.
        foreach (var t in addTo)
        {
            var items = _itemsCache.Load(userKey, t.PlaylistId) ?? new List<PlaylistItemDto>();
            int pos = items.Count == 0 ? 0 : items.Max(i => i.Position) + 1;
            _itemsCache.Save(userKey, t.PlaylistId,
                items.Append(new PlaylistItemDto(t.LocalItemId, req.VideoId, req.Title, req.ChannelTitle, pos, req.ThumbnailUrl)).ToList());
        }
        foreach (var r in removeFrom)
        {
            var items = _itemsCache.Load(userKey, r.PlaylistId);
            if (items is null) continue;
            _itemsCache.Save(userKey, r.PlaylistId, items.Where(i => i.PlaylistItemId != r.PlaylistItemId).ToList());
        }

        var move = new PendingSongMove
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            UserKey = userKey,
            VideoId = req.VideoId,
            Title = req.Title,
            ChannelTitle = req.ChannelTitle,
            ThumbnailUrl = req.ThumbnailUrl,
            AddTo = addTo,
            RemoveFrom = removeFrom,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _songMoves.Add(move);
        _touchStore.Touch(addTo.Select(t => t.PlaylistId).Concat(removeFrom.Select(r => r.PlaylistId)));
        _log.Add("SongAssign(local)", $"video={req.VideoId} add={addTo.Count} remove={removeFrom.Count} id={move.Id}");
        return ToDto(move);
    }

    public List<PendingSongMoveDto> GetPendingSongMoves() =>
        _songMoves.LoadForUser(CurrentUserKey()).Select(ToDto).ToList();

    /// <summary>Sube a YouTube la reasignación: inserta en AddTo y borra de RemoveFrom (parcial/reanudable).</summary>
    public async Task<SongMoveUploadResultDto> UploadSongMoveAsync(string id, CancellationToken ct = default)
    {
        var userKey = CurrentUserKey();
        var move = _songMoves.Get(id) ?? throw new ArgumentException("El cambio no existe (quizás ya se subió).");
        if (move.UserKey != userKey) throw new NotAuthenticatedException("Ese cambio es de otra cuenta.");

        var yt = BuildClient();
        int added = 0, removed = 0, failed = 0;
        bool paused = false;
        var addRem = new List<SongMoveTarget>();
        var remRem = new List<SongMoveRemoval>();
        var realIdByLocal = new Dictionary<string, string>(StringComparer.Ordinal);
        var known = KnownPlaylistIds();

        foreach (var t in move.AddTo)
        {
            if (paused) { addRem.Add(t); continue; }
            // Lista destino borrada: se omite en silencio (no es un fallo del usuario) y
            // se saca de la operación para que el pendiente pueda completarse.
            if (known is not null && !known.Contains(t.PlaylistId))
            {
                _logger.LogWarning("Lista {Pl} ya no existe; se omite el alta de {Video}.", t.PlaylistId, move.VideoId);
                continue;
            }
            try
            {
                var inserted = await yt.PlaylistItems.Insert(new PlaylistItem
                {
                    Snippet = new PlaylistItemSnippet { PlaylistId = t.PlaylistId, ResourceId = new ResourceId { Kind = "youtube#video", VideoId = move.VideoId } },
                }, "snippet").ExecuteAsync(ct);
                _quota.Add(50);
                _activity.Publish(new ActivityEvent("insert", move.Title, t.PlaylistTitle, move.VideoId, DateTime.UtcNow));
                realIdByLocal[t.LocalItemId] = inserted.Id;
                added++;
            }
            catch (Google.GoogleApiException ex) when (QuotaTracker.IsQuotaError(ex)) { _quota.MarkExhausted(); paused = true; addRem.Add(t); }
            catch (Google.GoogleApiException ex) { _logger.LogWarning(ex, "No se pudo agregar {Video} a {Pl}.", move.VideoId, t.PlaylistId); failed++; }
        }
        foreach (var r in move.RemoveFrom)
        {
            if (paused) { remRem.Add(r); continue; }
            // Si la lista entera ya no existe, la canción tampoco está en ella: hecho.
            if (known is not null && !known.Contains(r.PlaylistId)) { removed++; continue; }
            try
            {
                await yt.PlaylistItems.Delete(r.PlaylistItemId).ExecuteAsync(ct);
                _quota.Add(50);
                _activity.Publish(new ActivityEvent("delete", move.Title, r.PlaylistTitle, move.VideoId, DateTime.UtcNow));
                removed++;
            }
            catch (Google.GoogleApiException ex) when (QuotaTracker.IsQuotaError(ex)) { _quota.MarkExhausted(); paused = true; remRem.Add(r); }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { removed++; }
            catch (Google.GoogleApiException ex) { _logger.LogWarning(ex, "No se pudo quitar {Item} de {Pl}.", r.PlaylistItemId, r.PlaylistId); failed++; }
        }

        // Sincronizar ids reales en la caché de las playlists donde se agregó.
        foreach (var t in move.AddTo)
        {
            if (!realIdByLocal.TryGetValue(t.LocalItemId, out var realId)) continue;
            var items = _itemsCache.Load(userKey, t.PlaylistId);
            if (items is null) continue;
            _itemsCache.Save(userKey, t.PlaylistId,
                items.Select(i => i.PlaylistItemId == t.LocalItemId ? i with { PlaylistItemId = realId } : i).ToList());
        }

        int remainingOps = addRem.Count + remRem.Count;
        if (remainingOps == 0) _songMoves.Remove(id);
        else { move.AddTo = addRem; move.RemoveFrom = remRem; _songMoves.Replace(move); }

        _log.Add("SongAssign(upload)", $"id={id} video={move.VideoId} added={added} removed={removed} failed={failed} paused={paused} rem={remainingOps}");
        return new SongMoveUploadResultDto(id, move.VideoId, added, removed, failed, paused, remainingOps);
    }

    /// <summary>Descarta la reasignación y revierte el cambio local.</summary>
    public void DiscardSongMove(string id)
    {
        var userKey = CurrentUserKey();
        var move = _songMoves.Get(id);
        if (move is null) return;
        if (move.UserKey != userKey) throw new NotAuthenticatedException("Ese cambio es de otra cuenta.");

        foreach (var t in move.AddTo)
        {
            var items = _itemsCache.Load(userKey, t.PlaylistId);
            if (items is null) continue;
            _itemsCache.Save(userKey, t.PlaylistId, items.Where(i => i.PlaylistItemId != t.LocalItemId).ToList());
        }
        foreach (var r in move.RemoveFrom)
        {
            var items = _itemsCache.Load(userKey, r.PlaylistId) ?? new List<PlaylistItemDto>();
            if (items.Any(i => i.PlaylistItemId == r.PlaylistItemId)) continue;
            int pos = items.Count == 0 ? 0 : items.Max(i => i.Position) + 1;
            _itemsCache.Save(userKey, r.PlaylistId,
                items.Append(new PlaylistItemDto(r.PlaylistItemId, move.VideoId, move.Title, move.ChannelTitle, pos, move.ThumbnailUrl)).ToList());
        }
        _songMoves.Remove(id);
        _log.Add("SongAssign(discard)", $"id={id} video={move.VideoId}");
    }

    /// <summary>Playlists (ids) donde está actualmente la canción (solo caché, 0 cuota).</summary>
    public List<string> GetSongLocations(string videoId) =>
        CurrentSongLocations(CurrentUserKey(), videoId).Keys.ToList();

    /// <summary>Items de una lista SOLO desde caché (0 cuota, nunca toca YouTube). Vacío si no está cargada.</summary>
    public List<PlaylistItemDto> GetCachedItems(string playlistId) =>
        _itemsCache.Load(CurrentUserKey(), playlistId) ?? [];

    /// <summary>
    /// Encola la recuperación de canciones como un PendingUpload sin fuentes (nada que
    /// borrar): entra al panel de pendientes y se sube reanudable ante cuota agotada.
    /// Si no se indica lista destino, crea una nueva (50 unidades).
    /// </summary>
    public async Task<PendingUploadDto> StageRecoveryAsync(RecoverSongsRequest req, CancellationToken ct = default)
    {
        if (req.Songs is not { Count: > 0 })
            throw new ArgumentException("No hay canciones para recuperar.");

        var userKey = CurrentUserKey();
        string targetId;
        string targetTitle;

        if (!string.IsNullOrEmpty(req.TargetPlaylistId))
        {
            if (IsSpecialPlaylist(req.TargetPlaylistId))
                throw new ArgumentException(
                    "YouTube no permite modificar sus listas automáticas (Favoritos, Ver más tarde, " +
                    "Me gusta) desde la API. Elegí otra lista destino.");
            targetId = req.TargetPlaylistId;
            targetTitle = _cacheStore.Load()?.Playlists.FirstOrDefault(p => p.Id == targetId)?.Title ?? targetId;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(req.NewPlaylistTitle))
                throw new ArgumentException("Indicá una lista destino o el nombre de la lista nueva.");
            var yt = BuildClient();
            var created = await yt.Playlists.Insert(new Playlist
            {
                Snippet = new PlaylistSnippet { Title = req.NewPlaylistTitle.Trim() },
                Status = new PlaylistStatus { PrivacyStatus = "private" },
            }, "snippet,status").ExecuteAsync(ct);
            _quota.Add(50);
            targetId = created.Id;
            targetTitle = req.NewPlaylistTitle.Trim();
        }

        var plan = new PendingUpload
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            UserKey = userKey,
            TargetPlaylistId = targetId,
            TargetPlaylistTitle = targetTitle,
            Items = req.Songs
                .Where(s => !string.IsNullOrEmpty(s.VideoId))
                .DistinctBy(s => s.VideoId)
                .Select(s => new PendingUploadItem
                {
                    LocalItemId = $"recover-{Guid.NewGuid():N}",
                    VideoId = s.VideoId,
                    Title = s.Title,
                    ChannelTitle = s.ChannelTitle,
                    ThumbnailUrl = s.ThumbnailUrl,
                    FromPlaylists = [],
                })
                .ToList(),
            Sources = [],   // recuperación: no hay listas origen que borrar
            CreatedAtUtc = DateTime.UtcNow,
        };
        _pendingUploads.Add(plan);
        _log.Add("Recover(stage)", $"pending={plan.Id} target={targetId} items={plan.Items.Count} newList={string.IsNullOrEmpty(req.TargetPlaylistId)}");

        return new PendingUploadDto(
            plan.Id, plan.TargetPlaylistId, plan.TargetPlaylistTitle,
            plan.Items.Count, plan.Items.Count * 50, plan.CreatedAtUtc,
            plan.Items.Select(i => new PendingUploadItemDto(
                i.VideoId, i.Title, i.ChannelTitle ?? "", i.ThumbnailUrl, i.FromPlaylists)).ToList(),
            [], false, false);
    }

    /// <summary>
    /// Canciones "huérfanas": conocidas por la app (cachés de listas borradas + registro
    /// de actividad) pero ausentes de TODAS las playlists actuales. 0 cuota — solo disco.
    /// </summary>
    public List<RecoverableSongDto> GetRecoverableSongs()
    {
        // Ids de playlists vigentes según la caché de la lista (sin tocar YouTube).
        var currentPlaylists = _cacheStore.Load()?.Playlists.Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        var snapshot = _itemsCache.SnapshotAllPlaylists();
        var titleByPlaylist = _cacheStore.Load()?.Playlists
            .ToDictionary(p => p.Id, p => p.Title, StringComparer.Ordinal) ?? [];
        foreach (var a in _archivedStore.LoadAll())
            titleByPlaylist.TryAdd(a.Id, a.Title);

        // Presentes hoy: todo videoId en los items cacheados de playlists vigentes.
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (playlistId, entry) in snapshot)
            if (currentPlaylists.Contains(playlistId))
                foreach (var it in entry.Items)
                    if (!string.IsNullOrEmpty(it.VideoId)) present.Add(it.VideoId);

        var orphans = new Dictionary<string, RecoverableSongDto>(StringComparer.Ordinal);

        // Fuente 1: cachés de playlists que ya no existen (listas borradas cuya caché sobrevivió).
        foreach (var (playlistId, entry) in snapshot)
        {
            if (currentPlaylists.Contains(playlistId)) continue;
            var listName = titleByPlaylist.GetValueOrDefault(playlistId, playlistId);
            foreach (var it in entry.Items)
            {
                if (string.IsNullOrEmpty(it.VideoId) || present.Contains(it.VideoId)) continue;
                if (VideoAvailability.IsUnavailable(it.Title)) continue;
                if (!orphans.ContainsKey(it.VideoId))
                    orphans[it.VideoId] = new RecoverableSongDto(
                        it.VideoId, it.Title, it.ChannelTitle, it.ThumbnailUrl, listName, entry.CachedAtUtc);
            }
        }

        // Fuente 2: registro de actividad (inserts/deletes con videoId) — cubre listas cuya
        // caché ya se invalidó. El evento más reciente por video manda.
        foreach (var e in _activity.History(1000))
        {
            if (string.IsNullOrEmpty(e.VideoId) || present.Contains(e.VideoId)) continue;
            if (VideoAvailability.IsUnavailable(e.Title)) continue;
            if (orphans.TryGetValue(e.VideoId, out var cur) && cur.LastSeenUtc >= e.At) continue;
            orphans[e.VideoId] = new RecoverableSongDto(
                e.VideoId, e.Title, null,
                $"https://i.ytimg.com/vi/{e.VideoId}/default.jpg",
                e.Playlist, e.At);
        }

        return orphans.Values
            .OrderByDescending(o => o.LastSeenUtc)
            .ToList();
    }

    /// <summary>Sube TODA la cola de reasignaciones (corta y conserva el resto si se agota la cuota).</summary>
    public async Task<SongMoveBulkResultDto> UploadAllSongMovesAsync(CancellationToken ct = default)
    {
        var moves = _songMoves.LoadForUser(CurrentUserKey());
        int added = 0, removed = 0, failed = 0, completed = 0;
        bool paused = false;
        foreach (var m in moves)
        {
            if (paused) break;
            var r = await UploadSongMoveAsync(m.Id, ct);
            added += r.Added; removed += r.Removed; failed += r.Failed;
            if (r.Paused) paused = true; else completed++;
        }
        var remaining = _songMoves.LoadForUser(CurrentUserKey()).Count;
        return new SongMoveBulkResultDto(moves.Count, completed, added, removed, failed, paused, remaining);
    }

    /// <summary>Descarta TODA la cola de reasignaciones y revierte los cambios locales.</summary>
    public void DiscardAllSongMoves()
    {
        foreach (var m in _songMoves.LoadForUser(CurrentUserKey()))
            DiscardSongMove(m.Id);
    }

    /// <summary>Para un set de videoIds, las listas (ids) donde está cada uno (caché, 0 cuota).</summary>
    public Dictionary<string, List<string>> GetSongLocationsBatch(List<string> videoIds)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var want = new HashSet<string>(videoIds ?? [], StringComparer.Ordinal);
        if (want.Count == 0) return result;

        var userKey = CurrentUserKey();
        var cache = _cacheStore.Load();
        if (cache?.Playlists is null) return result;

        foreach (var pl in cache.Playlists)
        {
            var items = _itemsCache.Load(userKey, pl.Id);
            if (items is null) continue;
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.VideoId) || !want.Contains(it.VideoId)) continue;
                if (!result.TryGetValue(it.VideoId, out var list))
                {
                    list = [];
                    result[it.VideoId] = list;
                }
                if (!list.Contains(pl.Id)) list.Add(pl.Id);
            }
        }
        return result;
    }

    /// <summary>Encola quitar copias específicas (por playlistItemId) de una playlist. Local-first: deja en cola para sincronizar al Subir.</summary>
    public int StageRemoveItemsFromPlaylist(string playlistId, List<string> playlistItemIds)
    {
        if (string.IsNullOrEmpty(playlistId) || playlistItemIds is null || playlistItemIds.Count == 0) return 0;
        var userKey = CurrentUserKey();
        var cache = _cacheStore.Load();
        var title = cache?.Playlists?.FirstOrDefault(p => p.Id == playlistId)?.Title ?? playlistId;
        var items = _itemsCache.Load(userKey, playlistId);
        if (items is null) return 0;

        var targetIds = playlistItemIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var removedItemIds = new HashSet<string>(StringComparer.Ordinal);
        int staged = 0;
        foreach (var itemId in targetIds)
        {
            var hit = items.FirstOrDefault(i => i.PlaylistItemId == itemId);
            if (hit is null) continue;
            _songMoves.Add(new PendingSongMove
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                UserKey = userKey,
                VideoId = hit.VideoId,
                Title = hit.Title,
                ChannelTitle = hit.ChannelTitle,
                ThumbnailUrl = hit.ThumbnailUrl,
                AddTo = [],
                RemoveFrom = [new SongMoveRemoval { PlaylistId = playlistId, PlaylistTitle = title, PlaylistItemId = hit.PlaylistItemId }],
                CreatedAtUtc = DateTime.UtcNow,
            });
            removedItemIds.Add(hit.PlaylistItemId);
            staged++;
        }
        if (removedItemIds.Count > 0)
            _itemsCache.Save(userKey, playlistId, items.Where(i => !removedItemIds.Contains(i.PlaylistItemId)).ToList());

        if (staged > 0) _touchStore.Touch(playlistId);
        _log.Add("RemoveItems(local)", $"playlist={playlistId} staged={staged}");
        return staged;
    }

    public async Task<RefreshAllResultDto> RefreshAllAsync(CancellationToken ct = default)
    {
        var userKey = CurrentUserKey();
        var cache = _cacheStore.Load();

        // Si no hay caché de playlists, la primera llamada rellena la lista
        // (consume ~1u de quota, ~50 playlists por página).
        // Refrescar es justamente ignorar la caché: antes se saltaba toda lista que ya
        // tuviera items guardados, así que con la caché caliente el botón no hacía nada.
        var playlists = await GetMyPlaylistsAsync(ct, forceRefresh: true);

        int itemsRefreshed = 0;
        int playlistsRefreshed = 0;
        int playlistsSkipped = 0;
        int quotaUsed = 1;

        foreach (var pl in playlists)
        {
            try
            {
                var items = await GetPlaylistItemsAsync(pl.Id, ct, forceRefresh: true);
                itemsRefreshed += items.Count;
                playlistsRefreshed++;
                // ~1u cada 50 items, redondeado arriba.
                quotaUsed += Math.Max(1, (int)Math.Ceiling(items.Count / 50.0));
            }
            catch (Google.GoogleApiException ex)
            {
                playlistsSkipped++;
                _logger.LogWarning(ex, "No se pudo refrescar la playlist {Id}; se omite.", pl.Id);
                if (QuotaTracker.IsQuotaError(ex)) break;   // sin cuota no tiene sentido seguir
            }
        }

        _log.Add("RefreshAll", $"playlistsRefreshed={playlistsRefreshed} itemsRefreshed={itemsRefreshed} skipped={playlistsSkipped} quota~{quotaUsed}");
        return new RefreshAllResultDto(playlistsRefreshed, itemsRefreshed, playlistsSkipped, quotaUsed);
    }

    public Task<List<PlaylistArchivedInfoDto>> GetArchivedPlaylistsAsync(CancellationToken ct = default)
    {
        var byId = new Dictionary<string, PlaylistArchivedInfoDto>(StringComparer.Ordinal);
        foreach (var a in _archivedStore.LoadAll())
        {
            byId[a.Id] = new PlaylistArchivedInfoDto(
                Id: a.Id,
                Title: a.Title,
                ArchivedAt: a.ArchivedAtUtc,
                MergedIntoPlaylistId: a.MergedIntoPlaylistId,
                MergedIntoPlaylistTitle: a.MergedIntoPlaylistTitle,
                SongsCount: a.SongsCount);
        }

        // Recuperar listas borradas de merges anteriores que el flujo previo no
        // registró en el store de archivadas: se derivan de las revisiones de
        // merge que pidieron borrar las listas origen.
        foreach (var plan in _reviewStore.LoadAll().Where(p => p.DeleteSources))
        {
            foreach (var s in plan.Sources)
            {
                if (byId.ContainsKey(s.PlaylistId)) continue;
                byId[s.PlaylistId] = new PlaylistArchivedInfoDto(
                    Id: s.PlaylistId,
                    Title: s.Title,
                    ArchivedAt: plan.CreatedAtUtc,
                    MergedIntoPlaylistId: plan.TargetPlaylistId,
                    MergedIntoPlaylistTitle: plan.TargetPlaylistTitle,
                    SongsCount: s.ItemCount);
            }
        }

        var list = byId.Values.OrderByDescending(x => x.ArchivedAt).ToList();
        return Task.FromResult(list);
    }

    // ---- Helpers ----
    private static readonly Regex _nonWord = new(@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);
    private static readonly Regex _multiSpace = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] _noiseTokens =
    [
        "official", "video", "videoclip", "audio", "lyrics", "letra", "hd", "hq",
        "remastered", "remaster", "mv", "feat", "ft", "featuring", "cover"
    ];

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
        t = _nonWord.Replace(t, " ");
        var tokens = _multiSpace.Split(t).Where(x => x.Length > 1 && !_noiseTokens.Contains(x));
        return string.Join(" ", tokens).Trim();
    }
}
