namespace YTPlaylistManager.Server.Services;

public sealed class ArchivedPlaylistEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public DateTime ArchivedAtUtc { get; init; }
    public required string MergedIntoPlaylistId { get; init; }
    public required string MergedIntoPlaylistTitle { get; init; }
    public int SongsCount { get; init; }
}

/// <summary>
/// Persiste el registro de playlists "archivadas" localmente (porque se
/// consolidaron en otra). El concepto es local: no borra de YouTube.
/// </summary>
public sealed class ArchivedPlaylistsStore(IConfiguration cfg)
    : JsonListStore<ArchivedPlaylistEntry>(cfg, "archived-playlists.json")
{
    public void Add(IEnumerable<ArchivedPlaylistEntry> entries) => Mutate(list =>
    {
        foreach (var e in entries)
        {
            list.RemoveAll(x => x.Id == e.Id);
            list.Add(e);
        }
    });
}
