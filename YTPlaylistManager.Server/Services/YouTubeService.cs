using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
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
    private readonly ApiKeyPool _apiKeyPool;
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
        ApiKeyPool apiKeyPool,
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
        _apiKeyPool = apiKeyPool;
        _logger = logger;
    }

    /// <summary>Clave estable por cuenta (hash del refresh token). No gasta cuota.</summary>
    private string CurrentUserKey()
    {
        var t = _tokenStore.Load();
        var seed = t?.RefreshToken ?? t?.AccessToken ?? "anon";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>True si la excepción de Google es por cuota/límite de tasa (403).</summary>
    private static bool IsQuotaError(Google.GoogleApiException ex)
    {
        if (ex.HttpStatusCode != System.Net.HttpStatusCode.Forbidden) return false;
        var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason;
        return reason is "quotaExceeded" or "rateLimitExceeded" or "dailyLimitExceeded"
            || (ex.Message?.Contains("quota", StringComparison.OrdinalIgnoreCase) ?? false);
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
        if (archived.Count == 0)
        {
            return includeArchived ? source : source;
        }

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

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _cfg["Google:ClientId"],
                ClientSecret = _cfg["Google:ClientSecret"]
            },
            Scopes = _cfg.GetSection("Google:Scopes").Get<string[]>() ?? Array.Empty<string>()
        });

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
            return AnnotateQueued(AnnotateArchived(cache.Playlists, includeArchived));

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
            return AnnotateQueued(AnnotateArchived(ordered, includeArchived));
        }
        catch (Google.GoogleApiException ex)
        {
            // API falló (cuota/red). Si hay caché de esta misma cuenta, la usamos en vez de romper.
            if (cache is not null && cache.UserKey == userKey)
            {
                _logger.LogWarning(ex, "Fallo al listar playlists ({Status}); usando caché.", ex.HttpStatusCode);
                return AnnotateQueued(AnnotateArchived(cache.Playlists, includeArchived));
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
            var items = await FetchItemsFromApiAsync(playlistId, ct);
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

    private async Task<List<PlaylistItemDto>> FetchItemsFromApiAsync(string playlistId, CancellationToken ct)
    {
        // Pool de API keys activo → leemos items por API key (playlists públicas, sin OAuth) y rotamos
        // de key al agotarse la cuota. Suma la cuota de varios proyectos para las LECTURAS.
        if (_apiKeyPool.Enabled)
        {
            Google.GoogleApiException? lastQuota = null;
            foreach (var key in _apiKeyPool.Keys)
            {
                try
                {
                    return await FetchItemsAsync(BuildReadClient(key), playlistId, ct);
                }
                catch (Google.GoogleApiException ex) when (IsQuotaError(ex))
                {
                    lastQuota = ex;
                    _logger.LogWarning("API key sin cuota leyendo {Playlist}; probando la siguiente.", playlistId);
                }
            }
            if (lastQuota is not null) throw lastQuota; // todas las keys agotadas
        }

        // Sin pool → cliente OAuth normal (lee también privadas de la cuenta logueada).
        return await FetchItemsAsync(BuildClient(), playlistId, ct);
    }

    private static async Task<List<PlaylistItemDto>> FetchItemsAsync(
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

    /// <summary>Cliente de solo-lectura con API key (sin OAuth) — para contenido público.</summary>
    private Google.Apis.YouTube.v3.YouTubeService BuildReadClient(string apiKey)
        => new(new BaseClientService.Initializer { ApiKey = apiKey, ApplicationName = "YTPlaylistManager" });

    public async Task<DuplicateReportDto> FindDuplicatesAsync(string playlistId, CancellationToken ct = default)
    {
        var yt = BuildClient();
        var playlistReq = yt.Playlists.List("snippet");
        playlistReq.Id = playlistId;
        var pResp = await playlistReq.ExecuteAsync(ct);
        var playlistTitle = pResp.Items.FirstOrDefault()?.Snippet.Title ?? playlistId;

        var items = await GetPlaylistItemsAsync(playlistId, ct);

        var groups = new List<DuplicateGroupDto>();

        // Por videoId
        foreach (var g in items
                     .Where(x => !string.IsNullOrEmpty(x.VideoId))
                     .GroupBy(x => x.VideoId)
                     .Where(g => g.Count() > 1))
        {
            groups.Add(new DuplicateGroupDto(g.Key, "videoId", g.OrderBy(x => x.Position).ToList()));
        }

        // Por título normalizado (capta "misma canción con distinto video")
        var alreadyFlagged = new HashSet<string>(groups.SelectMany(g => g.Items).Select(i => i.PlaylistItemId));
        foreach (var g in items
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

        _log.Add("Merge(local)", $"sources={string.Join(",", req.SourcePlaylistIds)} target={targetId} staged={added} skipped={skipped} pendingId={pendingId}");

        return Task.FromResult(new MergePlaylistsResultDto(
            targetId, targetTitle, added, skipped, 0, pendingId, 0, false));
    }

    /// <summary>Sube a YouTube de verdad las canciones de un cambio pendiente (50u c/u).</summary>
    public async Task<UploadResultDto> UploadPendingAsync(string id, CancellationToken ct = default)
    {
        var userKey = CurrentUserKey();
        var plan = _pendingUploads.Get(id)
            ?? throw new ArgumentException("El cambio pendiente no existe (quizás ya se subió).");
        if (plan.UserKey != userKey)
            throw new NotAuthenticatedException("Ese cambio pendiente es de otra cuenta.");

        var yt = BuildClient();
        var targetId = plan.TargetPlaylistId;
        int uploaded = 0, failed = 0;
        bool paused = false;
        var remaining = new List<PendingUploadItem>();
        var realIdByLocal = new Dictionary<string, string>(StringComparer.Ordinal);  // localId -> id real de YouTube
        var failedLocalIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in plan.Items)
        {
            if (paused) { remaining.Add(item); continue; }
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
                realIdByLocal[item.LocalItemId] = inserted.Id;
                uploaded++;
            }
            catch (Google.GoogleApiException ex) when (IsQuotaError(ex))
            {
                paused = true;
                remaining.Add(item);
            }
            catch (Google.GoogleApiException ex)
            {
                _logger.LogWarning(ex, "No se pudo subir {Video} a {Target}.", item.VideoId, targetId);
                failedLocalIds.Add(item.LocalItemId);
                failed++;
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

        // Borrar las listas origen de YouTube SOLO cuando ya se subieron todas las canciones.
        int deletedSources = 0;
        var remainingSources = plan.Sources;
        if (remaining.Count == 0 && plan.Sources.Count > 0)
        {
            var stillPending = new List<PendingSource>();
            var deletedIds = new List<string>();
            foreach (var s in plan.Sources)
            {
                if (paused) { stillPending.Add(s); continue; }
                try
                {
                    await yt.Playlists.Delete(s.Id).ExecuteAsync(ct);
                    deletedIds.Add(s.Id);
                    _itemsCache.Invalidate(userKey, s.Id);
                }
                catch (Google.GoogleApiException ex) when (IsQuotaError(ex))
                {
                    paused = true;
                    stillPending.Add(s);
                }
                catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    deletedIds.Add(s.Id);   // ya no existía → la damos por borrada
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

        _log.Add("Upload", $"pending={id} target={targetId} uploaded={uploaded} failed={failed} deletedSources={deletedSources} paused={paused} remItems={remaining.Count} remSources={remainingSources.Count}");
        return new UploadResultDto(id, targetId, plan.TargetPlaylistTitle, uploaded, failed, paused, remaining.Count, deletedSources, remainingSources.Count);
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
        return _pendingUploads.LoadForUser(userKey)
            .Select(p => new PendingUploadDto(
                p.Id, p.TargetPlaylistId, p.TargetPlaylistTitle,
                p.Items.Count, (p.Items.Count + p.Sources.Count) * 50, p.CreatedAtUtc,
                p.Items.Select(i => new PendingUploadItemDto(
                    i.VideoId, i.Title, i.ChannelTitle ?? "", i.ThumbnailUrl, i.FromPlaylists)).ToList(),
                p.Sources.Select(s => s.Title).ToList()))
            .ToList();
    }

    /// <summary>Descarta un cambio pendiente y revierte la unión local del target.</summary>
    public void DiscardPending(string id)
    {
        var userKey = CurrentUserKey();
        var plan = _pendingUploads.Get(id);
        if (plan is null) return;
        if (plan.UserKey != userKey)
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

    private static PendingSongMoveDto ToDto(PendingSongMove m) => new(
        m.Id, m.VideoId, m.Title, m.ThumbnailUrl,
        m.AddTo.Select(a => a.PlaylistTitle).ToList(),
        m.RemoveFrom.Select(r => r.PlaylistTitle).ToList(),
        (m.AddTo.Count + m.RemoveFrom.Count) * 50,
        m.CreatedAtUtc);

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

        foreach (var t in move.AddTo)
        {
            if (paused) { addRem.Add(t); continue; }
            try
            {
                var inserted = await yt.PlaylistItems.Insert(new PlaylistItem
                {
                    Snippet = new PlaylistItemSnippet { PlaylistId = t.PlaylistId, ResourceId = new ResourceId { Kind = "youtube#video", VideoId = move.VideoId } },
                }, "snippet").ExecuteAsync(ct);
                realIdByLocal[t.LocalItemId] = inserted.Id;
                added++;
            }
            catch (Google.GoogleApiException ex) when (IsQuotaError(ex)) { paused = true; addRem.Add(t); }
            catch (Google.GoogleApiException ex) { _logger.LogWarning(ex, "No se pudo agregar {Video} a {Pl}.", move.VideoId, t.PlaylistId); failed++; }
        }
        foreach (var r in move.RemoveFrom)
        {
            if (paused) { remRem.Add(r); continue; }
            try
            {
                await yt.PlaylistItems.Delete(r.PlaylistItemId).ExecuteAsync(ct);
                removed++;
            }
            catch (Google.GoogleApiException ex) when (IsQuotaError(ex)) { paused = true; remRem.Add(r); }
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

    /// <summary>Encola (staged) quitar varias canciones de UNA playlist. Devuelve cuántas encoló.</summary>
    public int StageRemoveFromPlaylist(string playlistId, List<string> videoIds)
    {
        if (string.IsNullOrEmpty(playlistId) || videoIds is null || videoIds.Count == 0) return 0;
        var userKey = CurrentUserKey();
        var cache = _cacheStore.Load();
        var title = cache?.Playlists?.FirstOrDefault(p => p.Id == playlistId)?.Title ?? playlistId;
        var items = _itemsCache.Load(userKey, playlistId);
        if (items is null) return 0;

        var removedItemIds = new HashSet<string>(StringComparer.Ordinal);
        int staged = 0;
        foreach (var vid in videoIds.Distinct())
        {
            var hit = items.FirstOrDefault(i => i.VideoId == vid);
            if (hit is null) continue;
            _songMoves.Add(new PendingSongMove
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                UserKey = userKey,
                VideoId = vid,
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

        _log.Add("RemoveFromList(local)", $"playlist={playlistId} staged={staged}");
        return staged;
    }

    public async Task<RefreshAllResultDto> RefreshAllAsync(CancellationToken ct = default)
    {
        var userKey = CurrentUserKey();
        var cache = _cacheStore.Load();

        // Si no hay caché de playlists, la primera llamada rellena la lista
        // (consume ~1u de quota, ~50 playlists por página).
        var playlists = await GetMyPlaylistsAsync(ct, forceRefresh: cache?.Playlists is null or { Count: 0 });

        int itemsRefreshed = 0;
        int playlistsRefreshed = 0;
        int playlistsSkipped = 0;
        int quotaUsed = cache?.Playlists is null or { Count: 0 } ? 1 : 0;

        foreach (var pl in playlists)
        {
            // Si ya está en caché con items → no la releemos (ahorra cuota).
            var cached = _itemsCache.Load(userKey, pl.Id);
            if (cached is not null)
            {
                playlistsSkipped++;
                continue;
            }

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
                _logger.LogWarning(ex, "No se pudo refrescar la playlist {Id}; se omite.", pl.Id);
            }
        }

        _log.Add("RefreshAll", $"playlistsRefreshed={playlistsRefreshed} itemsRefreshed={itemsRefreshed} skipped={playlistsSkipped} quota~{quotaUsed}");
        return new RefreshAllResultDto(playlistsRefreshed, itemsRefreshed, playlistsSkipped, quotaUsed);
    }

    public Task<List<PlaylistArchivedInfoDto>> GetArchivedPlaylistsAsync(CancellationToken ct = default)
    {
        var archived = _archivedStore.LoadAll();
        var list = archived.Select(a => new PlaylistArchivedInfoDto(
            Id: a.Id,
            Title: a.Title,
            ArchivedAt: a.ArchivedAtUtc,
            MergedIntoPlaylistId: a.MergedIntoPlaylistId,
            MergedIntoPlaylistTitle: a.MergedIntoPlaylistTitle,
            SongsCount: a.SongsCount)).ToList();
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
