export interface Playlist {
  id: string;
  title: string;
  description?: string;
  itemCount: number;
  thumbnailUrl?: string;
  privacy?: string;            // "public" | "private" | "unlisted"
  isArchived?: boolean;
  archivedIntoPlaylistId?: string | null;
  archivedIntoPlaylistTitle?: string | null;
  queuedForMerge?: boolean;
  queuedIntoTitle?: string | null;
}

export interface PlaylistItem {
  playlistItemId: string;
  videoId: string;
  title: string;
  channelTitle?: string;
  position: number;
  thumbnailUrl?: string;
}

export interface DuplicateGroup {
  key: string;
  matchType: 'videoId' | 'normalizedTitle';
  items: PlaylistItem[];
}

export interface DuplicateReport {
  playlistId: string;
  playlistTitle: string;
  totalItems: number;
  duplicateCount: number;
  groups: DuplicateGroup[];
}

export interface MergeRequest {
  sourcePlaylistIds: string[];
  targetPlaylistId?: string | null;
  newPlaylistTitle?: string | null;
  deduplicateOnMerge: boolean;
  privacy: 'private' | 'unlisted' | 'public';
  deleteSources: boolean;
}

export interface MergeResult {
  targetPlaylistId: string;
  targetPlaylistTitle: string;
  added: number;
  skippedDuplicates: number;
  archivedSources: number;
  reviewId?: string | null;
  failed: number;
  paused: boolean;
}

export interface MergePreviewSong {
  videoId: string;
  title: string;
  channelTitle: string;
  thumbnailUrl?: string | null;
  fromPlaylists: string[];
}

export interface MergePreview {
  targetPlaylistId: string;
  targetPlaylistTitle: string;
  toAddCount: number;
  alreadyPresentCount: number;
  estimatedQuotaUnits: number;
  toAdd: MergePreviewSong[];
  warnings: string[];
}

export interface PendingUpload {
  id: string;
  targetPlaylistId: string;
  targetPlaylistTitle: string;
  itemCount: number;
  estimatedQuotaUnits: number;
  createdAtUtc: string;
  items: MergePreviewSong[];
  sourceTitles: string[];
}

export interface UploadResult {
  id: string;
  targetPlaylistId: string;
  targetPlaylistTitle: string;
  uploaded: number;
  failed: number;
  paused: boolean;
  remainingPending: number;
  deletedSources: number;
  remainingSources: number;
}

export interface ClassifiedSong {
  videoId: string;
  title: string;
  group: string;
}

export interface ClassifyResult {
  playlistId: string;
  mode: string;
  groups: Record<string, ClassifiedSong[]>;
}

export interface AuthStatus {
  isAuthenticated: boolean;
  expiresAtUtc?: string;
  hasRefreshToken: boolean;
}

export interface CrossPlaylistRef {
  playlistId: string;
  playlistTitle: string;
}

export interface CrossDuplicate {
  videoId: string;
  title: string;
  playlistCount: number;
  playlists: CrossPlaylistRef[];
}

export interface CrossDuplicateReport {
  totalPlaylists: number;
  totalGroups: number;
  groups: CrossDuplicate[];
  scanned: number;
  failed: number;
}

export interface PendingSongMove {
  id: string;
  videoId: string;
  title: string;
  thumbnailUrl?: string | null;
  addTo: string[];
  removeFrom: string[];
  estimatedQuotaUnits: number;
  createdAtUtc: string;
}

export interface SongMoveUploadResult {
  id: string;
  videoId: string;
  added: number;
  removed: number;
  failed: number;
  paused: boolean;
  remainingOps: number;
}

// ── Búsqueda bidireccional ──

export interface SongSearchQuery {
  videoIdPartial?: string | null;
  songNameFuzzy?: string | null;
  searchScope: string;
}

export interface SongSearchResult {
  videoId: string;
  title: string;
  channelTitle: string;
  originalPlaylistId: string;
  originalPlaylistTitle: string;
  originalPosition: number;
  currentPlaylistId?: string;
  currentPlaylistTitle?: string;
  currentPosition?: number;
  appearsInCount: number;
  appearsInPlaylistIds: string[];
  isDuplicate: boolean;
  wasMerged: boolean;
  mergeId?: string | null;
}

// ── Auditoría y trazabilidad ──

export interface SongMovementEvent {
  date: string;
  eventType: string;
  playlistId: string;
  playlistTitle: string;
  position: number;
  sourcePlaylistId?: string | null;
  reason?: string | null;
}

export interface SongMovementLog {
  videoId: string;
  songTitle: string;
  events: SongMovementEvent[];
}

export interface PlaylistArchivedInfo {
  id: string;
  title: string;
  archivedAt: string;
  mergedIntoPlaylistId: string;
  mergedIntoPlaylistTitle: string;
  songsCount: number;
}

export interface CacheStatus {
  playlistsCount: number;
  totalSongs: number;
  lastUpdated: string;
  mergesCount: number;
  archivedPlaylistsCount: number;
}

export interface MergeReviewSource {
  playlistId: string;
  title: string;
  itemCount: number;
}

export interface MergeReviewSummary {
  id: string;
  targetPlaylistId: string;
  targetPlaylistTitle: string;
  sources: MergeReviewSource[];
  newSongsCount: number;
  duplicateSongsCount: number;
  deleteSources: boolean;
  createdAtUtc: string;
}

// ── Pre-análisis de merge ──

