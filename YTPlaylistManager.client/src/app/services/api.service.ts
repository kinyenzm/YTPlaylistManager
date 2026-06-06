import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  Playlist, PlaylistItem, DuplicateReport, MergeRequest, MergeResult, MergePreview,
  PendingUpload, UploadResult, PendingSongMove, SongMoveUploadResult,
  ClassifyResult, AuthStatus, CrossDuplicateReport,
  SongSearchQuery, SongSearchResult, CacheStatus, SongMovementLog,
  PlaylistArchivedInfo, MergeReviewSummary
} from '../models/models';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // Base relativa ('/api'): en dev la sirve el proxy (src/proxy.conf.js),
  // en prod el mismo origen del backend que hospeda el SPA en wwwroot.
  private readonly base = environment.apiBaseUrl;

  // --- Auth ---
  loginUrl(): string { return `${this.base}/auth/login`; }
  authStatus(): Observable<AuthStatus> { return this.http.get<AuthStatus>(`${this.base}/auth/status`); }
  logout(): Observable<unknown> { return this.http.post(`${this.base}/auth/logout`, {}); }

  // --- Playlists ---
  listPlaylists(refresh = false, includeArchived = false): Observable<Playlist[]> {
    const params = new URLSearchParams();
    if (refresh) params.set('refresh', 'true');
    if (includeArchived) params.set('includeArchived', 'true');
    const qs = params.toString();
    return this.http.get<Playlist[]>(`${this.base}/playlists${qs ? '?' + qs : ''}`);
  }
  refreshAll(): Observable<{ playlistsRefreshed: number; itemsRefreshed: number; playlistsSkipped: number; quotaUsed: number }> {
    return this.http.post<{ playlistsRefreshed: number; itemsRefreshed: number; playlistsSkipped: number; quotaUsed: number }>(
      `${this.base}/playlists/refresh-all`, {},
    );
  }
  listItems(id: string): Observable<PlaylistItem[]> {
    return this.http.get<PlaylistItem[]>(`${this.base}/playlists/${id}/items`);
  }
  findDuplicates(id: string): Observable<DuplicateReport> {
    return this.http.get<DuplicateReport>(`${this.base}/playlists/${id}/duplicates`);
  }
  crossDuplicates(refresh = false): Observable<CrossDuplicateReport> {
    return this.http.get<CrossDuplicateReport>(`${this.base}/playlists/cross-duplicates${refresh ? '?refresh=true' : ''}`);
  }
  removeDuplicates(playlistId: string, strategy: 'videoId' | 'normalizedTitle'): Observable<{ removed: number; kept: number }> {
    return this.http.post<{ removed: number; kept: number }>(
      `${this.base}/playlists/remove-duplicates`,
      { playlistId, strategy }
    );
  }
  merge(req: MergeRequest): Observable<MergeResult> {
    return this.http.post<MergeResult>(`${this.base}/playlists/merge`, req);
  }
  previewMerge(targetPlaylistId: string, sourcePlaylistIds: string[]): Observable<MergePreview> {
    return this.http.post<MergePreview>(`${this.base}/playlists/merge/preview`, { targetPlaylistId, sourcePlaylistIds });
  }
  pendingUploads(): Observable<PendingUpload[]> {
    return this.http.get<PendingUpload[]>(`${this.base}/playlists/pending-uploads`);
  }
  uploadPending(id: string): Observable<UploadResult> {
    return this.http.post<UploadResult>(`${this.base}/playlists/pending-uploads/${id}/upload`, {});
  }
  discardPending(id: string): Observable<unknown> {
    return this.http.delete(`${this.base}/playlists/pending-uploads/${id}`);
  }

  // --- Asignar canción a playlists (staged) ---
  assignSong(req: {
    videoId: string;
    title: string;
    channelTitle?: string | null;
    thumbnailUrl?: string | null;
    desiredPlaylistIds: string[];
  }): Observable<PendingSongMove | null> {
    return this.http.post<PendingSongMove | null>(`${this.base}/songs/assign`, req);
  }
  pendingSongMoves(): Observable<PendingSongMove[]> {
    return this.http.get<PendingSongMove[]>(`${this.base}/songs/pending-moves`);
  }
  uploadSongMove(id: string): Observable<SongMoveUploadResult> {
    return this.http.post<SongMoveUploadResult>(`${this.base}/songs/pending-moves/${id}/upload`, {});
  }
  discardSongMove(id: string): Observable<unknown> {
    return this.http.delete(`${this.base}/songs/pending-moves/${id}`);
  }
  songLocations(videoId: string): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/songs/${videoId}/locations`);
  }
  songLocationsBatch(videoIds: string[]): Observable<Record<string, string[]>> {
    return this.http.post<Record<string, string[]>>(`${this.base}/songs/locations`, videoIds);
  }
  removeSongsFromPlaylist(playlistId: string, videoIds: string[]): Observable<{ staged: number }> {
    return this.http.post<{ staged: number }>(`${this.base}/songs/remove-from-playlist`, { playlistId, videoIds });
  }
  classify(id: string, mode: 'genre' | 'mood' | 'decade'): Observable<ClassifyResult> {
    return this.http.post<ClassifyResult>(`${this.base}/playlists/${id}/classify`, { playlistId: id, mode });
  }

  // --- Song Search (Bidirectional) ---
  searchSongs(query: SongSearchQuery): Observable<SongSearchResult[]> {
    return this.http.post<SongSearchResult[]>(`${this.base}/songs/search`, query);
  }

  // --- Cache Management ---
  getCacheStatus(): Observable<CacheStatus> {
    return this.http.get<CacheStatus>(`${this.base}/cache/status`);
  }

  getSongHistory(videoId: string): Observable<SongMovementLog> {
    return this.http.get<SongMovementLog>(`${this.base}/cache/song/${videoId}/history`);
  }

  getArchivedPlaylists(): Observable<PlaylistArchivedInfo[]> {
    return this.http.get<PlaylistArchivedInfo[]>(`${this.base}/cache/playlists-archived`);
  }
  getMergeReviews(): Observable<MergeReviewSummary[]> {
    return this.http.get<MergeReviewSummary[]>(`${this.base}/cache/merge-reviews`);
  }
}
