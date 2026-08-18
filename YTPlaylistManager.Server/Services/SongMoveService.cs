using Google.Apis.YouTube.v3.Data;
using YTPlaylistManager.Server.Domain.Entities;
using YTPlaylistManager.Server.Domain.Exceptions;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Reasignación de canciones entre listas (local-first): se aplica sobre la caché,
/// queda pendiente y se sube a YouTube cuando el usuario quiere. También resuelve
/// ubicaciones de canciones (0 cuota) y el encolado de remociones puntuales.
/// </summary>
public sealed class SongMoveService(
    YouTubeClientFactory clientFactory,
    PlaylistCatalog catalog,
    PlaylistCacheStore cacheStore,
    PlaylistItemsCacheStore itemsCache,
    PendingSongMoveStore songMoves,
    PlaylistTouchStore touchStore,
    ActivityBroadcaster activity,
    QuotaTracker quota,
    OperationLog log,
    ILogger<SongMoveService> logger)
{
    /// <summary>
    /// Playlists de la cuenta que contienen el videoId, con TODAS sus copias
    /// (playlistItemIds). Una lista puede tener la misma canción varias veces:
    /// quitarla de la lista debe quitar todas las copias, no solo la primera.
    /// </summary>
    private Dictionary<string, (string Title, List<string> ItemIds)> CurrentSongLocations(string userKey, string videoId)
    {
        var map = new Dictionary<string, (string, List<string>)>(StringComparer.Ordinal);
        var cache = cacheStore.Load();
        if (cache?.Playlists is null) return map;
        foreach (var pl in cache.Playlists)
        {
            var ids = itemsCache.Load(userKey, pl.Id)
                ?.Where(i => i.VideoId == videoId)
                .Select(i => i.PlaylistItemId)
                .ToList();
            if (ids is { Count: > 0 }) map[pl.Id] = (pl.Title, ids);
        }
        return map;
    }

    private PendingSongMoveDto ToDto(PendingSongMove m)
    {
        // Listas involucradas que ya no existen: se informan para que el panel lo avise;
        // al subir se omiten en vez de contarse como fallo.
        var known = catalog.KnownPlaylistIds();
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
            m.AddTo.Select(a => a.PlaylistTitle).Distinct().ToList(),
            m.RemoveFrom.Select(r => r.PlaylistTitle).Distinct().ToList(),
            (m.AddTo.Count + m.RemoveFrom.Count) * 50,
            m.CreatedAtUtc,
            missing);
    }

    /// <summary>Aplica en local la reasignación (agregar/quitar) y la deja pendiente de subir.</summary>
    public PendingSongMoveDto? StageSongAssignment(AssignSongRequest req)
    {
        if (string.IsNullOrEmpty(req.VideoId)) throw new ArgumentException("VideoId requerido.");
        var userKey = clientFactory.CurrentUserKey();
        var cache = cacheStore.Load();
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
            // Una remoción por copia: la subida borra cada playlistItem por separado.
            foreach (var itemId in info.ItemIds)
                removeFrom.Add(new SongMoveRemoval { PlaylistId = pid, PlaylistTitle = info.Title, PlaylistItemId = itemId });
        }

        if (addTo.Count == 0 && removeFrom.Count == 0) return null;

        // Aplicar en LOCAL: agregar items sintéticos / quitar de la caché.
        foreach (var t in addTo)
        {
            var items = itemsCache.Load(userKey, t.PlaylistId) ?? new List<PlaylistItemDto>();
            int pos = PlaylistCatalog.NextPosition(items);
            itemsCache.Save(userKey, t.PlaylistId,
                items.Append(new PlaylistItemDto(t.LocalItemId, req.VideoId, req.Title, req.ChannelTitle, pos, req.ThumbnailUrl)).ToList());
        }
        foreach (var r in removeFrom)
        {
            var items = itemsCache.Load(userKey, r.PlaylistId);
            if (items is null) continue;
            itemsCache.Save(userKey, r.PlaylistId, items.Where(i => i.PlaylistItemId != r.PlaylistItemId).ToList());
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
        songMoves.Add(move);
        touchStore.Touch(addTo.Select(t => t.PlaylistId).Concat(removeFrom.Select(r => r.PlaylistId)));
        log.Add("SongAssign(local)", $"video={req.VideoId} add={addTo.Count} remove={removeFrom.Count} id={move.Id}");
        return ToDto(move);
    }

    public List<PendingSongMoveDto> GetPendingSongMoves() =>
        songMoves.LoadForUser(clientFactory.CurrentUserKey()).Select(ToDto).ToList();

    /// <summary>Sube a YouTube la reasignación: inserta en AddTo y borra de RemoveFrom (parcial/reanudable).</summary>
    public async Task<SongMoveUploadResultDto> UploadSongMoveAsync(string id, CancellationToken ct = default)
    {
        var userKey = clientFactory.CurrentUserKey();
        var move = songMoves.Get(id) ?? throw new ArgumentException("El cambio no existe (quizás ya se subió).");
        if (move.UserKey != userKey) throw new NotAuthenticatedException("Ese cambio es de otra cuenta.");

        var yt = clientFactory.BuildClient();
        int added = 0, removed = 0, failed = 0;
        bool paused = false;
        var addRem = new List<SongMoveTarget>();
        var remRem = new List<SongMoveRemoval>();
        var realIdByLocal = new Dictionary<string, string>(StringComparer.Ordinal);
        var known = catalog.KnownPlaylistIds();

        foreach (var t in move.AddTo)
        {
            if (paused) { addRem.Add(t); continue; }
            // Lista destino borrada: se omite en silencio (no es un fallo del usuario) y
            // se saca de la operación para que el pendiente pueda completarse.
            if (known is not null && !known.Contains(t.PlaylistId))
            {
                logger.LogWarning("Lista {Pl} ya no existe; se omite el alta de {Video}.", t.PlaylistId, move.VideoId);
                continue;
            }
            try
            {
                var inserted = await yt.PlaylistItems.Insert(new PlaylistItem
                {
                    Snippet = new PlaylistItemSnippet { PlaylistId = t.PlaylistId, ResourceId = new ResourceId { Kind = "youtube#video", VideoId = move.VideoId } },
                }, "snippet").ExecuteAsync(ct);
                quota.Add(50);
                activity.Publish(new ActivityEvent("insert", move.Title, t.PlaylistTitle, move.VideoId, DateTime.UtcNow));
                realIdByLocal[t.LocalItemId] = inserted.Id;
                added++;
            }
            catch (Google.GoogleApiException ex) when (QuotaTracker.IsQuotaError(ex)) { quota.MarkExhausted(); paused = true; addRem.Add(t); }
            catch (Google.GoogleApiException ex) { logger.LogWarning(ex, "No se pudo agregar {Video} a {Pl}.", move.VideoId, t.PlaylistId); failed++; }
        }
        foreach (var r in move.RemoveFrom)
        {
            if (paused) { remRem.Add(r); continue; }
            // Si la lista entera ya no existe, la canción tampoco está en ella: hecho.
            if (known is not null && !known.Contains(r.PlaylistId)) { removed++; continue; }
            try
            {
                await yt.PlaylistItems.Delete(r.PlaylistItemId).ExecuteAsync(ct);
                quota.Add(50);
                activity.Publish(new ActivityEvent("delete", move.Title, r.PlaylistTitle, move.VideoId, DateTime.UtcNow));
                removed++;
            }
            catch (Google.GoogleApiException ex) when (QuotaTracker.IsQuotaError(ex)) { quota.MarkExhausted(); paused = true; remRem.Add(r); }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound) { removed++; }
            catch (Google.GoogleApiException ex) { logger.LogWarning(ex, "No se pudo quitar {Item} de {Pl}.", r.PlaylistItemId, r.PlaylistId); failed++; }
        }

        // Sincronizar ids reales en la caché de las playlists donde se agregó.
        foreach (var t in move.AddTo)
        {
            if (!realIdByLocal.TryGetValue(t.LocalItemId, out var realId)) continue;
            var items = itemsCache.Load(userKey, t.PlaylistId);
            if (items is null) continue;
            itemsCache.Save(userKey, t.PlaylistId,
                items.Select(i => i.PlaylistItemId == t.LocalItemId ? i with { PlaylistItemId = realId } : i).ToList());
        }

        int remainingOps = addRem.Count + remRem.Count;
        if (remainingOps == 0) songMoves.Remove(id);
        else { move.AddTo = addRem; move.RemoveFrom = remRem; songMoves.Replace(move); }

        log.Add("SongAssign(upload)", $"id={id} video={move.VideoId} added={added} removed={removed} failed={failed} paused={paused} rem={remainingOps}");
        return new SongMoveUploadResultDto(id, move.VideoId, added, removed, failed, paused, remainingOps);
    }

    /// <summary>Sube TODA la cola de reasignaciones (corta y conserva el resto si se agota la cuota).</summary>
    public async Task<SongMoveBulkResultDto> UploadAllSongMovesAsync(CancellationToken ct = default)
    {
        var moves = songMoves.LoadForUser(clientFactory.CurrentUserKey());
        int added = 0, removed = 0, failed = 0, completed = 0;
        bool paused = false;
        foreach (var m in moves)
        {
            if (paused) break;
            var r = await UploadSongMoveAsync(m.Id, ct);
            added += r.Added; removed += r.Removed; failed += r.Failed;
            if (r.Paused) paused = true; else completed++;
        }
        var remaining = songMoves.LoadForUser(clientFactory.CurrentUserKey()).Count;
        return new SongMoveBulkResultDto(moves.Count, completed, added, removed, failed, paused, remaining);
    }

    /// <summary>Descarta la reasignación y revierte el cambio local.</summary>
    public void DiscardSongMove(string id)
    {
        var userKey = clientFactory.CurrentUserKey();
        var move = songMoves.Get(id);
        if (move is null) return;
        if (move.UserKey != userKey) throw new NotAuthenticatedException("Ese cambio es de otra cuenta.");

        foreach (var t in move.AddTo)
        {
            var items = itemsCache.Load(userKey, t.PlaylistId);
            if (items is null) continue;
            itemsCache.Save(userKey, t.PlaylistId, items.Where(i => i.PlaylistItemId != t.LocalItemId).ToList());
        }
        foreach (var r in move.RemoveFrom)
        {
            var items = itemsCache.Load(userKey, r.PlaylistId) ?? new List<PlaylistItemDto>();
            if (items.Any(i => i.PlaylistItemId == r.PlaylistItemId)) continue;
            int pos = PlaylistCatalog.NextPosition(items);
            itemsCache.Save(userKey, r.PlaylistId,
                items.Append(new PlaylistItemDto(r.PlaylistItemId, move.VideoId, move.Title, move.ChannelTitle, pos, move.ThumbnailUrl)).ToList());
        }
        songMoves.Remove(id);
        log.Add("SongAssign(discard)", $"id={id} video={move.VideoId}");
    }

    /// <summary>Descarta TODA la cola de reasignaciones y revierte los cambios locales.</summary>
    public void DiscardAllSongMoves()
    {
        foreach (var m in songMoves.LoadForUser(clientFactory.CurrentUserKey()))
            DiscardSongMove(m.Id);
    }

    /// <summary>Playlists (ids) donde está actualmente la canción (solo caché, 0 cuota).</summary>
    public List<string> GetSongLocations(string videoId) =>
        GetSongLocationsBatch([videoId]).GetValueOrDefault(videoId) ?? [];

    /// <summary>Para un set de videoIds, las listas (ids) donde está cada uno (caché, 0 cuota).</summary>
    public Dictionary<string, List<string>> GetSongLocationsBatch(List<string> videoIds)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var want = new HashSet<string>(videoIds ?? [], StringComparer.Ordinal);
        if (want.Count == 0) return result;

        var userKey = clientFactory.CurrentUserKey();
        var cache = cacheStore.Load();
        if (cache?.Playlists is null) return result;

        foreach (var pl in cache.Playlists)
        {
            var items = itemsCache.Load(userKey, pl.Id);
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
        var userKey = clientFactory.CurrentUserKey();
        var title = catalog.TitleOf(playlistId);
        var items = itemsCache.Load(userKey, playlistId);
        if (items is null) return 0;

        var targetIds = playlistItemIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var hits = items.Where(i => targetIds.Contains(i.PlaylistItemId)).ToList();
        var removedItemIds = hits.Select(h => h.PlaylistItemId).ToHashSet(StringComparer.Ordinal);
        int staged = hits.Count;

        // Un pendiente por canción con todas sus copias: quitar 3 copias del mismo video
        // es UNA tarjeta en el panel (3 borrados al subir), no tres tarjetas.
        foreach (var byVideo in hits.GroupBy(h => h.VideoId))
        {
            var first = byVideo.First();
            songMoves.Add(new PendingSongMove
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                UserKey = userKey,
                VideoId = first.VideoId,
                Title = first.Title,
                ChannelTitle = first.ChannelTitle,
                ThumbnailUrl = first.ThumbnailUrl,
                AddTo = [],
                RemoveFrom = [.. byVideo.Select(h => new SongMoveRemoval
                {
                    PlaylistId = playlistId,
                    PlaylistTitle = title,
                    PlaylistItemId = h.PlaylistItemId,
                })],
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
        if (removedItemIds.Count > 0)
            itemsCache.Save(userKey, playlistId, items.Where(i => !removedItemIds.Contains(i.PlaylistItemId)).ToList());

        if (staged > 0) touchStore.Touch(playlistId);
        log.Add("RemoveItems(local)", $"playlist={playlistId} staged={staged}");
        return staged;
    }
}
