import { Component, ChangeDetectionStrategy, signal, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import {
  CrossDuplicateReport,
  Playlist,
  PlaylistItem,
  SongSearchResult,
  PendingSongMove,
  SongMoveUploadResult,
} from '../../models/models';

type Mode = 'repeated' | 'byList' | 'bySong';
interface SongRow {
  videoId: string;
  title: string;
}

@Component({
  selector: 'app-cross-duplicates',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FormsModule, TranslateModule],
  templateUrl: './cross-duplicates.html',
})
export class CrossDuplicates {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  protected readonly mode = signal<Mode>('repeated');
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly allPlaylists = signal<Playlist[]>([]);

  // id → título (para mostrar "en qué listas está")
  protected readonly titleById = computed<Record<string, string>>(() => {
    const m: Record<string, string> = {};
    for (const p of this.allPlaylists()) m[p.id] = p.title;
    return m;
  });
  // videoId → ids de listas (modo "por lista", cargado en lote)
  protected readonly locMap = signal<Record<string, string[]>>({});

  // Ordenados: primero las canciones que están en más de una playlist.
  protected readonly listItemsSorted = computed(() => {
    const m = this.locMap();
    return [...this.listItems()].sort(
      (a, b) => (m[b.videoId]?.length ?? 0) - (m[a.videoId]?.length ?? 0),
    );
  });
  protected readonly resultsSorted = computed(() =>
    [...this.results()].sort((a, b) => (b.appearsInCount ?? 0) - (a.appearsInCount ?? 0)),
  );
  protected readonly groupsSorted = computed(() => {
    const r = this.report();
    return r ? [...r.groups].sort((a, b) => b.playlistCount - a.playlistCount) : [];
  });

  thumb(videoId: string): string {
    return `https://i.ytimg.com/vi/${videoId}/default.jpg`;
  }
  listsFor(videoId: string): string[] {
    const t = this.titleById();
    return (this.locMap()[videoId] ?? []).map((id) => t[id] ?? id);
  }
  titlesOf(ids: string[]): string[] {
    const t = this.titleById();
    return ids.map((id) => t[id] ?? id);
  }

  // Modo "repetidas"
  protected readonly report = signal<CrossDuplicateReport | null>(null);

  // Modo "por lista"
  protected readonly listId = signal<string>('');
  protected readonly listItems = signal<PlaylistItem[]>([]);
  protected readonly bulkSelected = signal<ReadonlySet<string>>(new Set());

  // Modo "por canción"
  protected readonly term = signal<string>('');
  protected readonly results = signal<SongSearchResult[]>([]);

  // Editor de asignación (compartido, modal)
  protected readonly editingVideoId = signal<string | null>(null);
  protected readonly editingTitle = signal<string>('');
  protected readonly selection = signal<ReadonlySet<string>>(new Set());
  protected readonly singleMode = signal(false);
  protected readonly applying = signal(false);
  protected readonly editorLoading = signal(false);
  // Listas ordenadas para el modal: primero donde ya está, luego el resto (alfabético).
  protected readonly editorPlaylists = signal<Playlist[]>([]);

  // Cambios staged
  protected readonly pendingMoves = signal<PendingSongMove[]>([]);
  protected readonly uploadingId = signal<string | null>(null);
  protected readonly moveResult = signal<SongMoveUploadResult | null>(null);

  constructor() {
    this.loadPlaylists();
    this.loadPending();
  }

  setMode(m: Mode): void {
    this.mode.set(m);
    this.closeEditor();
    this.error.set(null);
  }

  private loadPlaylists(): void {
    this.api.listPlaylists().subscribe({
      next: (p) => this.allPlaylists.set(p.filter((x) => !x.isArchived)),
      error: (e) => console.error(e),
    });
  }

  loadPending(): void {
    this.api.pendingSongMoves().subscribe({
      next: (m) => this.pendingMoves.set(m),
      error: (e) => console.error(e),
    });
  }

  // ── Modo repetidas ──
  scan(refresh = false): void {
    this.loading.set(true);
    this.error.set(null);
    this.report.set(null);
    this.api.crossDuplicates(refresh).subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
        this.loadPlaylists();
      },
      error: (e) => {
        this.error.set(
          e?.status === 403
            ? this.translate.instant('common.youtube_quota_exhausted')
            : this.translate.instant('cross.error_scan'),
        );
        this.loading.set(false);
        console.error(e);
      },
    });
  }

  // ── Modo por lista ──
  pickList(id: string): void {
    this.listId.set(id);
    this.bulkSelected.set(new Set());
    this.closeEditor();
    if (!id) {
      this.listItems.set([]);
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    this.api.listItems(id, true).subscribe({   // solo caché: nunca lee de YouTube
      next: (items) => {
        this.listItems.set(items);
        this.loading.set(false);
        const ids = items.map((i) => i.videoId).filter(Boolean);
        if (ids.length) {
          this.api.songLocationsBatch(ids).subscribe({
            next: (m) => this.locMap.set(m),
            error: (e) => console.error(e),
          });
        }
      },
      error: (e) => {
        this.error.set(this.translate.instant('cross.error_scan'));
        this.loading.set(false);
        console.error(e);
      },
    });
  }

  toggleBulk(videoId: string): void {
    const n = new Set(this.bulkSelected());
    if (n.has(videoId)) n.delete(videoId);
    else n.add(videoId);
    this.bulkSelected.set(n);
  }

  removeBulk(): void {
    const ids = [...this.bulkSelected()];
    if (ids.length === 0) return;
    const listTitle = this.allPlaylists().find((p) => p.id === this.listId())?.title ?? '';
    if (!confirm(this.translate.instant('cross.bulk_remove_confirm', { n: ids.length, list: listTitle }))) return;
    this.api.removeSongsFromPlaylist(this.listId(), ids).subscribe({
      next: () => {
        this.bulkSelected.set(new Set());
        this.pickList(this.listId());
        this.loadPending();
      },
      error: (e) => {
        this.error.set(this.translate.instant('cross.assign_error'));
        console.error(e);
      },
    });
  }

  // ── Modo por canción ──
  search(): void {
    const q = this.term().trim();
    if (!q) return;
    this.loading.set(true);
    this.error.set(null);
    this.api.searchSongs({ videoIdPartial: q, songNameFuzzy: q, searchScope: 'all' }).subscribe({
      next: (r) => {
        this.results.set(r);
        this.loading.set(false);
      },
      error: (e) => {
        this.error.set(this.translate.instant('cross.error_scan'));
        this.loading.set(false);
        console.error(e);
      },
    });
  }

  // ── Editor de asignación ──
  openEditor(row: SongRow): void {
    this.editingVideoId.set(row.videoId);
    this.editingTitle.set(row.title);
    this.singleMode.set(false);
    this.moveResult.set(null);
    this.editorLoading.set(true);
    this.api.songLocations(row.videoId).subscribe({
      next: (locs) => {
        const sel = new Set(locs);
        this.selection.set(sel);
        // Primero las listas donde ya está; el resto alfabético (orden del backend).
        this.editorPlaylists.set(
          [...this.allPlaylists()].sort(
            (a, b) => (sel.has(b.id) ? 1 : 0) - (sel.has(a.id) ? 1 : 0),
          ),
        );
        this.editorLoading.set(false);
      },
      error: (e) => {
        this.selection.set(new Set());
        this.editorPlaylists.set([...this.allPlaylists()]);
        this.editorLoading.set(false);
        console.error(e);
      },
    });
  }

  closeEditor(): void {
    this.editingVideoId.set(null);
  }

  isChecked(pid: string): boolean {
    return this.selection().has(pid);
  }

  toggle(pid: string): void {
    if (this.singleMode()) {
      this.selection.set(new Set([pid]));
      return;
    }
    const n = new Set(this.selection());
    if (n.has(pid)) n.delete(pid);
    else n.add(pid);
    this.selection.set(n);
  }

  setSingle(v: boolean): void {
    this.singleMode.set(v);
    if (v && this.selection().size > 1) {
      const f = [...this.selection()][0];
      this.selection.set(new Set(f ? [f] : []));
    }
  }

  apply(): void {
    const vid = this.editingVideoId();
    if (!vid) return;
    this.applying.set(true);
    this.error.set(null);
    this.api
      .assignSong({
        videoId: vid,
        title: this.editingTitle(),
        channelTitle: null,
        thumbnailUrl: null,
        desiredPlaylistIds: [...this.selection()],
      })
      .subscribe({
        next: () => {
          this.applying.set(false);
          this.closeEditor();
          this.loadPending();
          this.refreshCurrentMode();
        },
        error: (e) => {
          this.error.set(this.translate.instant('cross.assign_error'));
          this.applying.set(false);
          console.error(e);
        },
      });
  }

  private refreshCurrentMode(): void {
    const m = this.mode();
    if (m === 'repeated' && this.report()) this.scan(false);
    else if (m === 'byList' && this.listId()) this.pickList(this.listId());
    else if (m === 'bySong' && this.results().length) this.search();
  }

  // ── Pendientes (staged) ──
  uploadMove(id: string): void {
    if (!confirm(this.translate.instant('cross.assign_upload_confirm'))) return;
    this.uploadingId.set(id);
    this.moveResult.set(null);
    this.error.set(null);
    this.api.uploadSongMove(id).subscribe({
      next: (r) => {
        this.moveResult.set(r);
        this.uploadingId.set(null);
        this.api.refreshQuota();
        this.loadPending();
        this.refreshCurrentMode();
      },
      error: (e) => {
        this.error.set(
          e?.status === 403
            ? this.translate.instant('common.youtube_quota_exhausted')
            : this.translate.instant('cross.assign_upload_error'),
        );
        this.uploadingId.set(null);
        console.error(e);
      },
    });
  }

  discardMove(id: string): void {
    if (!confirm(this.translate.instant('cross.assign_discard_confirm'))) return;
    this.api.discardSongMove(id).subscribe({
      next: () => {
        this.loadPending();
        this.refreshCurrentMode();
      },
      error: (e) => console.error(e),
    });
  }

  uploadAll(): void {
    if (!confirm(this.translate.instant('cross.upload_all_confirm', { n: this.pendingMoves().length }))) return;
    this.uploadingId.set('ALL');
    this.moveResult.set(null);
    this.error.set(null);
    this.api.uploadAllSongMoves().subscribe({
      next: () => {
        this.uploadingId.set(null);
        this.api.refreshQuota();
        this.loadPending();
        this.refreshCurrentMode();
      },
      error: (e) => {
        this.error.set(
          e?.status === 403
            ? this.translate.instant('common.youtube_quota_exhausted')
            : this.translate.instant('cross.assign_upload_error'),
        );
        this.uploadingId.set(null);
        console.error(e);
      },
    });
  }

  discardAll(): void {
    if (!confirm(this.translate.instant('cross.discard_all_confirm', { n: this.pendingMoves().length }))) return;
    this.api.discardAllSongMoves().subscribe({
      next: () => {
        this.loadPending();
        this.refreshCurrentMode();
      },
      error: (e) => console.error(e),
    });
  }
}
