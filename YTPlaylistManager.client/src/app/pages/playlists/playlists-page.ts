import { Component, ChangeDetectionStrategy, signal, computed, effect, inject, untracked, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { finalize } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { ApiErrorService } from '../../services/api-error.service';
import { AuthService } from '../../services/auth.service';
import { PendingService } from '../../services/pending.service';
import { RefreshAllService } from '../../services/refresh-all.service';
import { Playlist, MergeResult, MergePreview } from '../../models/models';
import { BusyOverlay } from '../../components/ui/busy-overlay';
import { EmptyState } from '../../components/ui/empty-state';
import { Modal } from '../../components/ui/modal';
import { SkeletonList } from '../../components/ui/skeleton-list';

@Component({
  selector: 'app-playlists-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, TranslateModule, BusyOverlay, EmptyState, Modal, SkeletonList],
  templateUrl: './playlists-page.html',
})
export class PlaylistsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);
  private readonly translate = inject(TranslateService);
  private readonly pending = inject(PendingService);
  private readonly refreshSvc = inject(RefreshAllService);
  private readonly auth = inject(AuthService);

  protected readonly authChecked = this.auth.checked;
  protected readonly authenticated = this.auth.connected;

  constructor() {
    // Recargar las listas cuando el panel global sube/descarta cambios
    // (las uniones subidas borran listas origen).
    effect(() => {
      if (this.pending.mutations() === 0) return;
      untracked(() => this.load());
    });

    // El refresco global terminó (quizás mientras se navegaba): releer de la caché nueva.
    effect(() => {
      if (this.refreshSvc.result() === null) return;
      untracked(() => this.load());
    });

    // Cargar con sesión; al desconectar, limpiar (sin sesión no se muestra caché).
    effect(() => {
      if (this.authenticated()) {
        untracked(() => {
          this.load();
          this.pending.refresh();
        });
      } else {
        untracked(() => {
          this.playlists.set([]);
          this.selectedIds.set(new Set());
          this.loading.set(false);
        });
      }
    });
  }

  protected readonly playlists = signal<Playlist[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly selectedIds = signal<ReadonlySet<string>>(new Set());
  protected readonly merging = signal(false);
  protected readonly mergeResult = signal<MergeResult | null>(null);
  protected readonly preview = signal<MergePreview | null>(null);
  protected readonly previewing = signal(false);

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

  // Recientes primero (acción local registrada); sin fecha → después, en el
  // orden alfabético que ya trae el backend.
  protected readonly playlistsSorted = computed<Playlist[]>(() =>
    [...this.playlists()].sort((a, b) => {
      const ta = a.lastModifiedUtc ? Date.parse(a.lastModifiedUtc) : 0;
      const tb = b.lastModifiedUtc ? Date.parse(b.lastModifiedUtc) : 0;
      if (ta !== tb) return tb - ta;
      return a.title.localeCompare(b.title);
    }),
  );

  modifiedAgo(iso: string): string {
    const ms = Date.parse(iso) - Date.now();
    const rtf = new Intl.RelativeTimeFormat(this.translate.currentLang || 'es', { numeric: 'auto' });
    const minutes = Math.round(ms / 60000);
    if (Math.abs(minutes) < 60) return rtf.format(minutes, 'minute');
    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) return rtf.format(hours, 'hour');
    return rtf.format(Math.round(hours / 24), 'day');
  }

  protected readonly refreshConfirmOpen = signal(false);
  protected readonly refreshing = this.refreshSvc.running;
  protected readonly refreshResult = this.refreshSvc.result;
  protected readonly refreshError = this.refreshSvc.errorMsg;

  protected readonly refreshEstimate = computed<{ playlists: number; quota: number }>(() => {
    const list = this.activePlaylists();
    if (list.length === 0) return { playlists: 0, quota: 0 };
    return { playlists: list.length, quota: list.length + 1 };
  });

  ngOnInit(): void {
    // La carga inicial la maneja el effect sobre authenticated(); si el shell aún
    // no verificó la sesión, evitamos el skeleton infinito.
    if (!this.auth.checked()) this.loading.set(true);
  }

  load(refresh = false): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.listPlaylists(refresh)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (list) => this.playlists.set(list),
        error: (e) => this.error.set(this.apiError.message(e, 'playlists.error_load')),
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

  // Selección por click en toda la card; archivadas/en cola no son seleccionables.
  toggleCard(p: Playlist): void {
    if (p.isArchived || p.queuedForMerge) return;
    this.toggle(p.id);
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
    this.api.previewMerge(targetId, sourceIds)
      .pipe(finalize(() => this.previewing.set(false)))
      .subscribe({
        next: (p) => this.preview.set(p),
        error: (e) => this.error.set(this.apiError.message(e, 'playlists.error_merge')),
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
      .pipe(finalize(() => this.merging.set(false)))
      .subscribe({
        next: (r) => {
          this.mergeResult.set(r);
          this.selectedIds.set(new Set());
          this.load();
          this.pending.refresh();
        },
        error: (e) => this.error.set(this.apiError.message(e, 'playlists.error_merge')),
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
    this.error.set(null);
    this.refreshSvc.start();
  }

  dismissRefreshResult(): void {
    this.refreshSvc.dismiss();
  }
}
