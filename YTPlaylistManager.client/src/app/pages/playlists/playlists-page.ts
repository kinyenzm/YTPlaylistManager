import { Component, ChangeDetectionStrategy, signal, computed, inject, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { Playlist, MergeResult, MergePreview, PendingUpload, UploadResult } from '../../models/models';

interface RefreshAllResult {
  playlistsRefreshed: number;
  itemsRefreshed: number;
  playlistsSkipped: number;
  quotaUsed: number;
}

@Component({
  selector: 'app-playlists-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, TranslateModule],
  templateUrl: './playlists-page.html',
})
export class PlaylistsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  protected readonly playlists = signal<Playlist[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly selectedIds = signal<ReadonlySet<string>>(new Set());
  protected readonly merging = signal(false);
  protected readonly mergeResult = signal<MergeResult | null>(null);
  protected readonly preview = signal<MergePreview | null>(null);
  protected readonly previewing = signal(false);

  // Cambios aplicados en local, pendientes de subir a YouTube.
  protected readonly pendingUploads = signal<PendingUpload[]>([]);
  protected readonly showPendingPanel = signal(false);
  protected readonly uploadingId = signal<string | null>(null);
  protected readonly uploadResult = signal<UploadResult | null>(null);

  protected readonly pendingTotalSongs = computed<number>(() =>
    this.pendingUploads().reduce((n, p) => n + p.itemCount, 0),
  );

  protected readonly selectedPlaylists = computed<Playlist[]>(() =>
    this.playlists()
      .filter((p) => this.selectedIds().has(p.id) && !p.isArchived && !p.queuedForMerge)
      .sort((a, b) => b.itemCount - a.itemCount),
  );

  protected readonly targetOverride = signal<string | null>(null);
  protected readonly targetId = computed<string | null>(() => {
    const sel = this.selectedPlaylists();
    if (sel.length === 0) return null;
    const ov = this.targetOverride();
    return ov && sel.some((p) => p.id === ov) ? ov : sel[0].id;
  });

  protected readonly activePlaylists = computed<Playlist[]>(() =>
    this.playlists().filter((p) => !p.isArchived),
  );

  protected readonly authChecked = signal(false);
  protected readonly authenticated = signal(false);

  protected readonly refreshConfirmOpen = signal(false);
  protected readonly refreshing = signal(false);
  protected readonly refreshResult = signal<RefreshAllResult | null>(null);

  protected readonly refreshEstimate = computed<{ playlists: number; quota: number }>(() => {
    const list = this.activePlaylists();
    if (list.length === 0) return { playlists: 0, quota: 0 };
    return { playlists: list.length, quota: list.length + 1 };
  });

  ngOnInit(): void {
    this.api.authStatus().subscribe({
      next: (s) => {
        this.authenticated.set(s.isAuthenticated);
        this.authChecked.set(true);
        if (s.isAuthenticated) {
          this.load();
          this.loadPendingUploads();
        }
      },
      error: () => this.authChecked.set(true),
    });
  }

  loadPendingUploads(): void {
    this.api.pendingUploads().subscribe({
      next: (p) => this.pendingUploads.set(p),
      error: (e) => console.error(e),
    });
  }

  openPendingPanel(): void {
    this.uploadResult.set(null);
    this.showPendingPanel.set(true);
  }

  closePendingPanel(): void {
    this.showPendingPanel.set(false);
  }

  uploadPending(id: string): void {
    const pu = this.pendingUploads().find((p) => p.id === id);
    if (pu) {
      const msg = this.translate.instant('playlists.upload_confirm', {
        songs: pu.itemCount,
        sources: pu.sourceTitles.join(', ') || '—',
      });
      if (!confirm(msg)) return;
    }
    this.uploadingId.set(id);
    this.uploadResult.set(null);
    this.error.set(null);
    this.api.uploadPending(id).subscribe({
      next: (r) => {
        this.uploadResult.set(r);
        this.uploadingId.set(null);
        this.loadPendingUploads();
        this.load();
      },
      error: (e) => {
        this.error.set(
          e?.status === 403
            ? this.translate.instant('common.youtube_quota_exhausted')
            : this.translate.instant('playlists.upload_error'),
        );
        this.uploadingId.set(null);
        console.error(e);
      },
    });
  }

  discardPending(id: string): void {
    if (!confirm(this.translate.instant('playlists.pending_discard_confirm'))) return;
    this.api.discardPending(id).subscribe({
      next: () => {
        this.loadPendingUploads();
        this.load();
      },
      error: (e) => console.error(e),
    });
  }

  load(refresh = false): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.listPlaylists(refresh).subscribe({
      next: (list) => {
        this.playlists.set(list);
        this.loading.set(false);
      },
      error: (e) => {
        this.error.set(
          e?.status === 403
            ? this.translate.instant('common.youtube_quota_exhausted')
            : this.translate.instant('playlists.error_load'),
        );
        this.loading.set(false);
        console.error(e);
      },
    });
  }

  toggle(id: string): void {
    const next = new Set(this.selectedIds());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.selectedIds.set(next);
  }

  openPreview(): void {
    const targetId = this.targetId();
    if (!targetId) return;
    const sourceIds = Array.from(this.selectedIds()).filter((id) => id !== targetId);
    if (sourceIds.length === 0) {
      this.error.set(this.translate.instant('playlists.error_need_two'));
      return;
    }
    this.previewing.set(true);
    this.error.set(null);
    this.api.previewMerge(targetId, sourceIds).subscribe({
      next: (p) => {
        this.preview.set(p);
        this.previewing.set(false);
      },
      error: (e) => {
        this.error.set(this.translate.instant('playlists.error_merge'));
        this.previewing.set(false);
        console.error(e);
      },
    });
  }

  cancelPreview(): void {
    this.preview.set(null);
  }

  merge(): void {
    const targetId = this.targetId();
    if (!targetId) return;
    const sourceIds = Array.from(this.selectedIds()).filter((id) => id !== targetId);
    if (sourceIds.length === 0) {
      this.error.set(this.translate.instant('playlists.error_need_two'));
      return;
    }

    this.preview.set(null);
    this.merging.set(true);
    this.mergeResult.set(null);
    this.api
      .merge({
        sourcePlaylistIds: sourceIds,
        newPlaylistTitle: null,
        targetPlaylistId: targetId,
        deduplicateOnMerge: true,
        privacy: 'private',
        deleteSources: false,
      })
      .subscribe({
        next: (r) => {
          this.mergeResult.set(r);
          this.merging.set(false);
          this.selectedIds.set(new Set());
          this.load();
          this.loadPendingUploads();
        },
        error: (e) => {
          this.error.set(this.translate.instant('playlists.error_merge'));
          this.merging.set(false);
          console.error(e);
        },
      });
  }

  openRefreshConfirm(): void {
    if (this.refreshing()) return;
    this.refreshResult.set(null);
    this.refreshConfirmOpen.set(true);
  }

  cancelRefresh(): void {
    this.refreshConfirmOpen.set(false);
  }

  confirmRefresh(): void {
    this.refreshConfirmOpen.set(false);
    this.refreshing.set(true);
    this.error.set(null);
    this.api.refreshAll().subscribe({
      next: (r) => {
        this.refreshResult.set(r);
        this.refreshing.set(false);
        this.load();
      },
      error: (e) => {
        this.error.set(this.translate.instant('playlists.refresh_all_error'));
        this.refreshing.set(false);
        console.error(e);
      },
    });
  }

  dismissRefreshResult(): void {
    this.refreshResult.set(null);
  }
}
