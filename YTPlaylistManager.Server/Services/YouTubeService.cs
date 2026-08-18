using Google.Apis.YouTube.v3.Data;
using YTPlaylistManager.Server.Domain.Entities;
using YTPlaylistManager.Server.Domain.Exceptions;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Lectura de playlists (con caché y fallback) y el ciclo de unión: preview,
/// staging local, subida reanudable y descarte. Duplicados, recuperación y
/// reasignaciones viven en sus propios servicios.
/// </summary>
public sealed class YouTubeService(
    YouTubeClientFactory clientFactory,
    PlaylistCatalog catalog,
    OperationLog log,
    PlaylistCacheStore cacheStore,
    PlaylistItemsCacheStore itemsCache,
    ArchivedPlaylistsStore archivedStore,
    MergeReviewStore reviewStore,
    PendingUploadStore pendingUploads,
    QuotaTracker quota,
    ActivityBroadcaster activity,
    PlaylistTouchStore touchStore,
    ILogger<YouTubeService> logger) : IYouTubeService
{
    // ── Lectura de playlists ──

    /// <summary>Anota la última modificación local registrada (PlaylistTouchStore).</summary>
    private List<PlaylistDto> AnnotateTouched(List<PlaylistDto> source)
    {
        var touched = touchStore.LoadAll();
        if (touched.Count == 0) return source;
        return source
            .Select(p => touched.TryGetValue(p.Id, out var at) ? p with { LastModifiedUtc = at } : p)
            .ToList();
    }

    /// <summary>Marca las playlists que son origen de un cambio pendiente (en cola de unir).</summary>
    private List<PlaylistDto> AnnotateQueued(List<PlaylistDto> source)
    {
        var pending = pendingUploads.LoadForUser(clientFactory.CurrentUserKey());
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
        var archived = archivedStore.LoadAll();
        if (archived.Count == 0) return source;

        var archivedById = archived.ToDictionary(a => a.Id);
        var annotated = source.Select(p => archivedById.TryGetValue(p.Id, out var a)
            ? p with { IsArchived = true, ArchivedIntoPlaylistId = a.MergedIntoPlaylistId, ArchivedIntoPlaylistTitle = a.MergedIntoPlaylistTitle }
            : p).ToList();

        return includeArchived ? annotated : annotated.Where(p => !p.IsArchived).ToList();
    }

    public async Task<List<PlaylistDto>> GetMyPlaylistsAsync(CancellationToken ct = default, bool forceRefresh = false, bool includeArchived = false)
    {
        var userKey = clientFactory.CurrentUserKey();
        var cache = cacheStore.Load();

        // Caché de la misma cuenta y sin pedir refrescar → servimos del archivo (0 cuota).
        if (!forceRefresh && cache is not null && cache.UserKey == userKey)
            return AnnotateTouched(AnnotateQueued(AnnotateArchived(cache.Playlists, includeArchived)));

        try
        {
            var yt = clientFactory.BuildClient();
            var result = new List<PlaylistDto>();
            string? pageToken = null;

            do
            {
                var req = yt.Playlists.List("snippet,contentDetails,status");
                req.Mine = true;
                req.MaxResults = 50;
                req.PageToken = pageToken;
                var resp = await req.ExecuteAsync(ct);
                quota.Add(1);

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
            cacheStore.Save(new PlaylistCache { UserKey = userKey, CachedAtUtc = DateTime.UtcNow, Playlists = ordered });
            return AnnotateTouched(AnnotateQueued(AnnotateArchived(ordered, includeArchived)));
        }
        catch (Google.GoogleApiException ex)
        {
            // API falló (cuota/red). Si hay caché de esta misma cuenta, la usamos en vez de romper.
            if (cache is not null && cache.UserKey == userKey)
            {
                logger.LogWarning(ex, "Fallo al listar playlists ({Status}); usando caché.", ex.HttpStatusCode);
                return AnnotateTouched(AnnotateQueued(AnnotateArchived(cache.Playlists, includeArchived)));
            }
            throw;
        }
    }

    public async Task<List<PlaylistItemDto>> GetPlaylistItemsAsync(string playlistId, CancellationToken ct = default, bool forceRefresh = false)
    {
        var userKey = clientFactory.CurrentUserKey();

        // Caché de items: si ya leímos esta playlist y no se pide refrescar → 0 cuota.
        if (!forceRefresh)
        {
            var cached = itemsCache.Load(userKey, playlistId);
            if (cached is not null) return cached;
        }

        try
        {
            var items = await FetchItemsAsync(clientFactory.BuildClient(), playlistId, ct);
            itemsCache.Save(userKey, playlistId, items);  // guardar para no re-leer
            return items;
        }
        catch (Google.GoogleApiException ex)
        {
            // API falló (cuota/red). Si hay caché de esta playlist, la usamos en vez de romper.
            var cached = itemsCache.Load(userKey, playlistId);
            if (cached is not null)
            {
                logger.LogWarning(ex, "Items de {Playlist}: API falló ({Status}); usando caché.", playlistId, ex.HttpStatusCode);
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
            quota.Add(1);

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

    /// <summary>Items de una lista SOLO desde caché (0 cuota, nunca toca YouTube). Vacío si no está cargada.</summary>
    public List<PlaylistItemDto> GetCachedItems(string playlistId) =>
        itemsCache.Load(clientFactory.CurrentUserKey(), playlistId) ?? [];

    public async Task<RefreshAllResultDto> RefreshAllAsync(CancellationToken ct = default)
    {
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
                logger.LogWarning(ex, "No se pudo refrescar la playlist {Id}; se omite.", pl.Id);
                if (QuotaTracker.IsQuotaError(ex)) break;   // sin cuota no tiene sentido seguir
            }
        }

        log.Add("RefreshAll", $"playlistsRefreshed={playlistsRefreshed} itemsRefreshed={itemsRefreshed} skipped={playlistsSkipped} quota~{quotaUsed}");
        return new RefreshAllResultDto(playlistsRefreshed, itemsRefreshed, playlistsSkipped, quotaUsed);
    }

    // ── Unión de playlists (local-first) ──

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
        var userKey = clientFactory.CurrentUserKey();
        var targetId = req.TargetPlaylistId;
        var targetTitle = catalog.TitleOf(targetId);
        var warnings = new List<string>();

        var targetItems = itemsCache.Load(userKey, targetId);
        if (targetItems is null)
            warnings.Add($"La lista destino «{targetTitle}» no está cargada; ábrela o usa «Actualizar» para un cálculo exacto.");

        var existing = new HashSet<string>(
            (targetItems ?? new List<PlaylistItemDto>())
                .Where(i => !string.IsNullOrEmpty(i.VideoId)).Select(i => i.VideoId),
            StringComparer.Ordinal);

        var byVideo = new Dictionary<string, PreviewAccum>(StringComparer.Ordinal);
        int alreadyPresent = 0;

        foreach (var src in req.SourcePlaylistIds.Distinct())
        {
            if (src == targetId) continue;
            var srcTitle = catalog.TitleOf(src);
            var items = itemsCache.Load(userKey, src);
            if (items is null)
            {
                warnings.Add($"La lista «{srcTitle}» no está cargada; ábrela para incluirla en la vista previa.");
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
        var userKey = clientFactory.CurrentUserKey();

        if (string.IsNullOrEmpty(req.TargetPlaylistId))
            throw new ArgumentException("TargetPlaylistId es requerido (merge SIEMPRE hacia una playlist existente).");
        if (req.SourcePlaylistIds is null || req.SourcePlaylistIds.Count == 0)
            throw new ArgumentException("SourcePlaylistIds no puede estar vacío.");
        if (PlaylistCatalog.IsSpecialPlaylist(req.TargetPlaylistId))
            throw new ArgumentException(
                "YouTube no permite modificar sus listas automáticas (Favoritos, Ver más tarde, " +
                "Me gusta) desde la API. Elige otra lista destino.");

        var targetId = req.TargetPlaylistId;
        var targetTitle = catalog.TitleOf(targetId);

        // Unión EN LOCAL (0 cuota): trabajamos sobre la caché de items.
        var targetItems = itemsCache.Load(userKey, targetId) ?? new List<PlaylistItemDto>();
        var existing = new HashSet<string>(
            targetItems.Where(i => !string.IsNullOrEmpty(i.VideoId)).Select(i => i.VideoId),
            StringComparer.Ordinal);

        // Agrupar por videoId las canciones que faltan (una fila por canción + listas origen).
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);
        var byVideo = new Dictionary<string, PreviewAccum>(StringComparer.Ordinal);
        var sourcesUsed = new List<PendingSource>();
        int skipped = 0;
        int nextPosition = PlaylistCatalog.NextPosition(targetItems);

        foreach (var src in req.SourcePlaylistIds.Distinct())
        {
            if (src == targetId) continue;
            var srcTitle = catalog.TitleOf(src);
            var items = itemsCache.Load(userKey, src);
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
            itemsCache.Save(userKey, targetId, targetItems.Concat(newCacheItems).ToList());

        int added = pendingItems.Count;
        string? pendingId = null;
        // Crear el pendiente aunque no haya canciones nuevas: si hay listas origen,
        // unir = borrarlas y conservar la destino (quedan en cola para borrar al subir).
        if (added > 0 || sourcesUsed.Count > 0)
        {
            pendingId = Guid.NewGuid().ToString("N")[..12];
            pendingUploads.Add(new PendingUpload
            {
                Id = pendingId,
                UserKey = userKey,
                TargetPlaylistId = targetId,
                TargetPlaylistTitle = targetTitle,
                Items = pendingItems,
                Sources = sourcesUsed,
                CreatedAtUtc = DateTime.UtcNow,
            });
            touchStore.Touch(targetId);
        }

        log.Add("Merge(local)", $"sources={string.Join(",", req.SourcePlaylistIds)} target={targetId} staged={added} skipped={skipped} pendingId={pendingId}");

        return Task.FromResult(new MergePlaylistsResultDto(
            targetId, targetTitle, added, skipped, 0, pendingId, 0, false));
    }

    // ── Subida de pendientes ──

    /// <summary>Resultado interno de la inserción de items de un plan.</summary>
    private sealed class InsertOutcome
    {
        public int Uploaded;
        public int Failed;
        public bool Paused;
        public bool TargetMissing;
        public List<PendingUploadItem> Remaining = [];
        public Dictionary<string, string> RealIdByLocal = new(StringComparer.Ordinal);  // localId -> id real
        public HashSet<string> FailedLocalIds = new(StringComparer.Ordinal);
    }

    /// <summary>Sube a YouTube de verdad las canciones de un cambio pendiente (50u c/u).
    /// <paramref name="limit"/> limita el número de canciones a subir en esta llamada; sin valor sube todo.</summary>
    public async Task<UploadResultDto> UploadPendingAsync(string id, int? limit = null, CancellationToken ct = default)
    {
        var userKey = clientFactory.CurrentUserKey();
        var plan = pendingUploads.Get(id)
            ?? throw new ArgumentException("El cambio pendiente no existe (quizás ya se subió).");
        if (plan.UserKey != userKey)
            throw new NotAuthenticatedException("Ese cambio pendiente es de otra cuenta.");

        // Las listas automáticas de YouTube (FL/WL/LL/RD) no admiten escritura por API desde
        // 2016: el insert responde 404 aunque la lista exista y se vea en la web.
        if (PlaylistCatalog.IsSpecialPlaylist(plan.TargetPlaylistId))
            return new UploadResultDto(id, plan.TargetPlaylistId, plan.TargetPlaylistTitle,
                0, 0, false, plan.Items.Count, 0, plan.Sources.Count, TargetMissing: false, TargetLocked: true);

        // Validación previa (0 cuota): si la lista destino ya no existe, cortar acá —
        // sin gastar unidades ni tocar la caché ni las listas origen. No es un error de
        // la petición sino un estado del mundo, así que se informa en el resultado.
        if (catalog.IsKnownMissing(plan.TargetPlaylistId))
            return new UploadResultDto(id, plan.TargetPlaylistId, plan.TargetPlaylistTitle,
                0, 0, false, plan.Items.Count, 0, plan.Sources.Count, TargetMissing: true);

        var yt = clientFactory.BuildClient();
        var targetId = plan.TargetPlaylistId;

        var insert = await InsertPendingItemsAsync(yt, plan, limit, ct);
        ReconcileTargetCache(userKey, targetId, insert);
        var (deletedSources, remainingSources, paused) =
            await DeleteSourcesWhenCompleteAsync(yt, userKey, plan, insert, ct);

        if (insert.Remaining.Count == 0 && remainingSources.Count == 0)
        {
            pendingUploads.Remove(id);
        }
        else
        {
            plan.Items = insert.Remaining;
            plan.Sources = remainingSources;
            pendingUploads.Replace(plan);
        }

        log.Add("Upload", $"pending={id} target={targetId} uploaded={insert.Uploaded} failed={insert.Failed} deletedSources={deletedSources} paused={paused} remItems={insert.Remaining.Count} remSources={remainingSources.Count} targetMissing={insert.TargetMissing}");

        // YouTube respondió 404: la lista destino ya no existe. Se saca de la caché de
        // listas para que el próximo listado de pendientes ya venga con TargetMissing y
        // la UI oculte el botón de subir, en vez de chocar contra el mismo 404 cada vez.
        if (insert.TargetMissing) catalog.RemoveFromListCache([targetId]);

        return new UploadResultDto(id, targetId, plan.TargetPlaylistTitle, insert.Uploaded, insert.Failed, paused,
            insert.Remaining.Count, deletedSources, remainingSources.Count, insert.TargetMissing);
    }

    /// <summary>Inserta las canciones del plan en la lista destino (50u c/u), hasta el límite.</summary>
    private async Task<InsertOutcome> InsertPendingItemsAsync(
        Google.Apis.YouTube.v3.YouTubeService yt, PendingUpload plan, int? limit, CancellationToken ct)
    {
        var r = new InsertOutcome();
        int processed = 0;

        foreach (var item in plan.Items)
        {
            if (r.Paused || r.TargetMissing || (limit.HasValue && processed >= limit.Value)) { r.Remaining.Add(item); continue; }
            try
            {
                var inserted = await yt.PlaylistItems.Insert(new PlaylistItem
                {
                    Snippet = new PlaylistItemSnippet
                    {
                        PlaylistId = plan.TargetPlaylistId,
                        ResourceId = new ResourceId { Kind = "youtube#video", VideoId = item.VideoId },
                    },
                }, "snippet").ExecuteAsync(ct);
                quota.Add(50);
                activity.Publish(new ActivityEvent("insert", item.Title, plan.TargetPlaylistTitle, item.VideoId, DateTime.UtcNow));
                r.RealIdByLocal[item.LocalItemId] = inserted.Id;
                r.Uploaded++;
                processed++;
            }
            catch (Google.GoogleApiException ex) when (QuotaTracker.IsQuotaError(ex))
            {
                quota.MarkExhausted();
                r.Paused = true;
                r.Remaining.Add(item);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Target inexistente (borrado después de encolar): abortar TODO el plan en
                // vez de iterar fallando item por item; el resto queda pendiente y las
                // fuentes NO se tocan.
                logger.LogWarning(ex, "Target {Target} no existe; se aborta la subida del pendiente {Id}.", plan.TargetPlaylistId, plan.Id);
                r.TargetMissing = true;
                r.Remaining.Add(item);
            }
            catch (Google.GoogleApiException ex)
            {
                logger.LogWarning(ex, "No se pudo subir {Video} a {Target}.", item.VideoId, plan.TargetPlaylistId);
                r.FailedLocalIds.Add(item.LocalItemId);
                r.Failed++;
                processed++;
            }
        }

        return r;
    }

    /// <summary>
    /// Actualiza la caché del target EN EL LUGAR (sin leer de YouTube): cambia los ids
    /// sintéticos por los reales del insert y quita los que fallaron.
    /// </summary>
    private void ReconcileTargetCache(string userKey, string targetId, InsertOutcome insert)
    {
        if (insert.RealIdByLocal.Count == 0 && insert.FailedLocalIds.Count == 0) return;
        var cached = itemsCache.Load(userKey, targetId);
        if (cached is null) return;

        var updated = cached
            .Where(i => !insert.FailedLocalIds.Contains(i.PlaylistItemId))
            .Select(i => insert.RealIdByLocal.TryGetValue(i.PlaylistItemId, out var realId)
                ? i with { PlaylistItemId = realId }
                : i)
            .ToList();
        itemsCache.Save(userKey, targetId, updated);
    }

    /// <summary>
    /// Borra las listas origen de YouTube SOLO cuando TODAS las canciones se subieron de
    /// verdad: sin restantes, sin fallos y con el target vivo. Un plan con fallos queda
    /// pendiente (descartable a mano) — nunca se borra una fuente sin haber copiado su
    /// contenido.
    /// </summary>
    private async Task<(int Deleted, List<PendingSource> Remaining, bool Paused)> DeleteSourcesWhenCompleteAsync(
        Google.Apis.YouTube.v3.YouTubeService yt, string userKey, PendingUpload plan, InsertOutcome insert, CancellationToken ct)
    {
        bool paused = insert.Paused;
        if (insert.Remaining.Count != 0 || insert.Failed != 0 || insert.TargetMissing || plan.Sources.Count == 0)
            return (0, plan.Sources, paused);

        var stillPending = new List<PendingSource>();
        var deletedIds = new List<string>();
        var archivedEntries = new List<ArchivedPlaylistEntry>();

        ArchivedPlaylistEntry Archived(PendingSource s, int songsCount) => new()
        {
            Id = s.Id,
            Title = s.Title,
            ArchivedAtUtc = DateTime.UtcNow,
            MergedIntoPlaylistId = plan.TargetPlaylistId,
            MergedIntoPlaylistTitle = plan.TargetPlaylistTitle,
            SongsCount = songsCount,
        };

        foreach (var s in plan.Sources)
        {
            if (paused) { stillPending.Add(s); continue; }
            // Playlists especiales (FL/WL/LL/RD): la API de YouTube no permite borrarlas.
            // Las consideramos "completadas" para que el pending no quede bloqueado.
            if (PlaylistCatalog.IsSpecialPlaylist(s.Id))
            {
                logger.LogWarning("Lista origen {Id} ({Title}) es especial de YouTube y no se puede borrar via API; se omite.", s.Id, s.Title);
                itemsCache.Invalidate(userKey, s.Id);
                continue;
            }
            var songsCount = itemsCache.Load(userKey, s.Id)?.Count ?? 0;
            try
            {
                await yt.Playlists.Delete(s.Id).ExecuteAsync(ct);
                quota.Add(50);
                activity.Publish(new ActivityEvent("delete-list", s.Title, "", "", DateTime.UtcNow));
                deletedIds.Add(s.Id);
                archivedEntries.Add(Archived(s, songsCount));
                itemsCache.Invalidate(userKey, s.Id);
            }
            catch (Google.GoogleApiException ex) when (QuotaTracker.IsQuotaError(ex))
            {
                quota.MarkExhausted();
                paused = true;
                stillPending.Add(s);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                deletedIds.Add(s.Id);   // ya no existía → la damos por borrada
                archivedEntries.Add(Archived(s, songsCount));
                itemsCache.Invalidate(userKey, s.Id);
            }
            catch (Google.GoogleApiException ex)
            {
                logger.LogWarning(ex, "No se pudo borrar la lista origen {Source}.", s.Id);
                stillPending.Add(s);
            }
        }

        if (archivedEntries.Count > 0) archivedStore.Add(archivedEntries);
        catalog.RemoveFromListCache(deletedIds);
        return (deletedIds.Count, stillPending, paused);
    }

    public List<PendingUploadDto> GetPendingUploads()
    {
        var userKey = clientFactory.CurrentUserKey();
        var known = catalog.KnownPlaylistIds();
        return [.. pendingUploads.LoadForUser(userKey)
                .Select(p => new PendingUploadDto(
                    p.Id, p.TargetPlaylistId, p.TargetPlaylistTitle,
                    p.Items.Count, (p.Items.Count + p.Sources.Count) * 50, p.CreatedAtUtc,
                [.. p.Items.Select(i => new PendingUploadItemDto(i.VideoId, i.Title, i.ChannelTitle ?? "", i.ThumbnailUrl, i.FromPlaylists))],
                [.. p.Sources.Select(s => s.Title)],
                    !PlaylistCatalog.IsSpecialPlaylist(p.TargetPlaylistId) && known is not null && !known.Contains(p.TargetPlaylistId),
                    PlaylistCatalog.IsSpecialPlaylist(p.TargetPlaylistId)))
               ];
    }

    /// <summary>Descarta un cambio pendiente y revierte la unión local del target.</summary>
    public void DiscardPending(string id)
    {
        var userKey = clientFactory.CurrentUserKey();
        var plan = pendingUploads.Get(id);
        if (plan is null) return;
        if (plan.UserKey != userKey && plan.Items.Count > 0)
            throw new NotAuthenticatedException("Ese cambio pendiente es de otra cuenta.");

        var localIds = plan.Items.Select(i => i.LocalItemId).ToHashSet(StringComparer.Ordinal);
        var targetItems = itemsCache.Load(userKey, plan.TargetPlaylistId);
        if (targetItems is not null)
            itemsCache.Save(userKey, plan.TargetPlaylistId,
                targetItems.Where(i => !localIds.Contains(i.PlaylistItemId)).ToList());

        pendingUploads.Remove(id);
        log.Add("DiscardPending", $"pending={id} target={plan.TargetPlaylistId} reverted={plan.Items.Count}");
    }

    // ── Archivadas ──

    public Task<List<PlaylistArchivedInfoDto>> GetArchivedPlaylistsAsync(CancellationToken ct = default)
    {
        var byId = new Dictionary<string, PlaylistArchivedInfoDto>(StringComparer.Ordinal);
        foreach (var a in archivedStore.LoadAll())
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
        foreach (var plan in reviewStore.LoadAll().Where(p => p.DeleteSources))
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
}
