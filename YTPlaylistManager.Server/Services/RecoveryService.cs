using Google.Apis.YouTube.v3.Data;
using YTPlaylistManager.Server.Domain.Entities;
using YTPlaylistManager.Server.DTOs;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Recuperación de canciones huérfanas: las que la app conoce (cachés de listas
/// borradas + registro de actividad) pero que no están en ninguna playlist actual.
/// </summary>
public sealed class RecoveryService(
    YouTubeClientFactory clientFactory,
    PlaylistCatalog catalog,
    PlaylistCacheStore cacheStore,
    PlaylistItemsCacheStore itemsCache,
    ArchivedPlaylistsStore archivedStore,
    PendingUploadStore pendingUploads,
    ActivityBroadcaster activity,
    QuotaTracker quota,
    OperationLog log)
{
    /// <summary>
    /// Canciones "huérfanas": conocidas por la app pero ausentes de TODAS las playlists
    /// actuales. 0 cuota — solo disco.
    /// </summary>
    public List<RecoverableSongDto> GetRecoverableSongs()
    {
        // Ids de playlists vigentes según la caché de la lista (sin tocar YouTube).
        var currentPlaylists = catalog.KnownPlaylistIds() ?? [];

        var snapshot = itemsCache.SnapshotAllPlaylists();
        var titleByPlaylist = cacheStore.Load()?.Playlists
            .ToDictionary(p => p.Id, p => p.Title, StringComparer.Ordinal) ?? [];
        foreach (var a in archivedStore.LoadAll())
            titleByPlaylist.TryAdd(a.Id, a.Title);

        // Presentes hoy: todo videoId en los items cacheados de playlists vigentes, y
        // sus títulos normalizados — para distinguir "no existe en ninguna forma" de
        // "existe la misma canción con otro video" (misma música, otra subida).
        var present = new HashSet<string>(StringComparer.Ordinal);
        var presentTitles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (playlistId, entry) in snapshot)
            if (currentPlaylists.Contains(playlistId))
                foreach (var it in entry.Items)
                {
                    if (string.IsNullOrEmpty(it.VideoId)) continue;
                    present.Add(it.VideoId);
                    if (VideoAvailability.IsUnavailable(it.Title)) continue;
                    var norm = DuplicateService.Normalize(it.Title);
                    if (!string.IsNullOrWhiteSpace(norm)) presentTitles.Add(norm);
                }

        bool ExistsByTitle(string title)
        {
            var norm = DuplicateService.Normalize(title);
            return !string.IsNullOrWhiteSpace(norm) && presentTitles.Contains(norm);
        }

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
                        it.VideoId, it.Title, it.ChannelTitle, it.ThumbnailUrl, listName, entry.CachedAtUtc,
                        ExistsByTitle(it.Title));
            }
        }

        // Fuente 2: registro de actividad (inserts/deletes con videoId) — cubre listas cuya
        // caché ya se invalidó. El evento más reciente por video manda.
        foreach (var e in activity.History(1000))
        {
            if (string.IsNullOrEmpty(e.VideoId) || present.Contains(e.VideoId)) continue;
            if (VideoAvailability.IsUnavailable(e.Title)) continue;
            if (orphans.TryGetValue(e.VideoId, out var cur) && cur.LastSeenUtc >= e.At) continue;
            orphans[e.VideoId] = new RecoverableSongDto(
                e.VideoId, e.Title, null,
                $"https://i.ytimg.com/vi/{e.VideoId}/default.jpg",
                e.Playlist, e.At,
                ExistsByTitle(e.Title));
        }

        return orphans.Values
            .OrderByDescending(o => o.LastSeenUtc)
            .ToList();
    }

    /// <summary>
    /// Encola la recuperación de canciones como un PendingUpload sin fuentes (nada que
    /// borrar): entra al panel de pendientes y se sube reanudable ante cuota agotada.
    /// Si no se indica lista destino, crea una nueva (50 unidades).
    /// </summary>
    public async Task<PendingUploadDto> StageRecoveryAsync(RecoverSongsRequest req, CancellationToken ct = default)
    {
        if (req.Songs is not { Count: > 0 })
            throw new ArgumentException("No hay canciones para recuperar.");

        var userKey = clientFactory.CurrentUserKey();
        string targetId;
        string targetTitle;

        if (!string.IsNullOrEmpty(req.TargetPlaylistId))
        {
            if (PlaylistCatalog.IsSpecialPlaylist(req.TargetPlaylistId))
                throw new ArgumentException(
                    "YouTube no permite modificar sus listas automáticas (Favoritos, Ver más tarde, " +
                    "Me gusta) desde la API. Elige otra lista destino.");
            targetId = req.TargetPlaylistId;
            targetTitle = catalog.TitleOf(targetId);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(req.NewPlaylistTitle))
                throw new ArgumentException("Indica una lista destino o el nombre de la lista nueva.");
            var yt = clientFactory.BuildClient();
            var created = await yt.Playlists.Insert(new Playlist
            {
                Snippet = new PlaylistSnippet { Title = req.NewPlaylistTitle.Trim() },
                Status = new PlaylistStatus { PrivacyStatus = "private" },
            }, "snippet,status").ExecuteAsync(ct);
            quota.Add(50);
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
        pendingUploads.Add(plan);
        log.Add("Recover(stage)", $"pending={plan.Id} target={targetId} items={plan.Items.Count} newList={string.IsNullOrEmpty(req.TargetPlaylistId)}");

        return new PendingUploadDto(
            plan.Id, plan.TargetPlaylistId, plan.TargetPlaylistTitle,
            plan.Items.Count, plan.Items.Count * 50, plan.CreatedAtUtc,
            plan.Items.Select(i => new PendingUploadItemDto(
                i.VideoId, i.Title, i.ChannelTitle ?? "", i.ThumbnailUrl, i.FromPlaylists)).ToList(),
            [], false, false);
    }
}
