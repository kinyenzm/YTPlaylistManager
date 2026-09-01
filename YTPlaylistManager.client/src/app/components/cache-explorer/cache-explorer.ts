import { Component, ChangeDetectionStrategy, signal, inject, input, effect, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { catchError, finalize, forkJoin, of } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { ApiErrorService } from '../../services/api-error.service';
import { CacheStatus, PlaylistArchivedInfo, MergeReviewSummary, ActivityItem, BackupStatus } from '../../models/models';
import { SkeletonList } from '../ui/skeleton-list';
import { SongHistory } from '../song-history/song-history';

type Tab = 'dashboard' | 'archived' | 'reviews' | 'activity' | 'history' | 'backup';

@Component({
  selector: 'app-cache-explorer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, TranslateModule, SkeletonList, SongHistory],
  templateUrl: './cache-explorer.html',
})
export class CacheExplorer {
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);
  private readonly auth = inject(AuthService);
  private readonly translate = inject(TranslateService);
  protected readonly connected = this.auth.connected;
  protected readonly driveBackupEnabled = this.auth.driveBackupEnabled;
  // Reconectar forzando el consent: agrega el scope de Drive al grant.
  protected readonly reconnectUrl = this.api.loginUrl() + '?consent=true';

  cacheStatus = signal<CacheStatus | null>(null);
  archivedPlaylists = signal<PlaylistArchivedInfo[]>([]);
  mergeReviews = signal<MergeReviewSummary[]>([]);
  activityLog = signal<ActivityItem[]>([]);

  isLoading = signal(false);
  error = signal<string | null>(null);

  activeTab = signal<Tab>('dashboard');

  // ── Respaldo ──
  backupStatus = signal<BackupStatus | null>(null);
  backupBusy = signal<'export' | 'import' | 'drive-up' | 'drive-down' | null>(null);
  backupMsg = signal<string | null>(null);
  backupError = signal<string | null>(null);

  // Deep-link: el "Ver más" del panel flotante navega con ?tab=activity.
  readonly tab = input<string>();

  constructor() {
    effect(() => {
      if (this.tab() === 'activity') this.activeTab.set('activity');
    });

    // El backend exige sesión también para el caché ("sin sesión no se muestra ni
    // caché"): cargar solo conectado y vaciar todo al desconectar.
    effect(() => {
      if (this.connected()) {
        untracked(() => {
          this.loadCacheData();
          this.loadActivity();
        });
      } else {
        untracked(() => {
          this.cacheStatus.set(null);
          this.archivedPlaylists.set([]);
          this.mergeReviews.set([]);
          this.activityLog.set([]);
          this.error.set(null);
          this.isLoading.set(false);
        });
      }
    });
  }

  loadActivity(): void {
    this.api.getActivityLog(1000).subscribe({
      next: (log) => this.activityLog.set(log),
      error: (err) => console.error('Error loading activity log:', err),
    });
  }

  // Las tres lecturas son independientes: van en paralelo. Solo el estado del
  // caché es obligatorio; archivadas y revisiones degradan a vacío si fallan.
  loadCacheData(): void {
    this.isLoading.set(true);
    this.error.set(null);

    forkJoin({
      status: this.api.getCacheStatus(),
      archived: this.api.getArchivedPlaylists().pipe(catchError(() => of([] as PlaylistArchivedInfo[]))),
      reviews: this.api.getMergeReviews().pipe(catchError(() => of([] as MergeReviewSummary[]))),
    })
      .pipe(finalize(() => this.isLoading.set(false)))
      .subscribe({
        next: ({ status, archived, reviews }) => {
          this.cacheStatus.set(status);
          this.archivedPlaylists.set(archived);
          this.mergeReviews.set(reviews);
        },
        error: (e) => this.error.set(this.apiError.message(e, 'cache.error_load', { msg: (e as Error)?.message })),
      });
  }

  switchTab(tab: Tab): void {
    this.activeTab.set(tab);
    if (tab === 'backup' && this.connected()) this.loadBackupStatus();
  }

  // ── Respaldo ──

  loadBackupStatus(): void {
    this.api.backupStatus().subscribe({
      next: (s) => this.backupStatus.set(s),
      error: () => this.backupStatus.set(null),
    });
  }

  downloadBackup(): void {
    this.backupBusy.set('export');
    this.backupError.set(null);
    this.api.exportBackup().subscribe({
      next: (blob) => {
        this.backupBusy.set(null);
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `ytpm-backup-${new Date().toISOString().slice(0, 10)}.json`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: (e) => {
        this.backupBusy.set(null);
        this.backupError.set(this.backupErrText(e));
      },
    });
  }

  onImportFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';   // permite re-elegir el mismo archivo después
    if (!file) return;
    if (!confirm(this.translate.instant('cache.backup_import_confirm'))) return;
    this.backupBusy.set('import');
    this.backupError.set(null);
    this.api.importBackup(file).subscribe({
      // El estado en memoria del cliente (listas, borradores, pendientes) quedó
      // obsoleto tras reemplazar el registro: recargar la app es lo honesto.
      next: () => location.reload(),
      error: (e) => {
        this.backupBusy.set(null);
        this.backupError.set(this.backupErrText(e));
      },
    });
  }

  driveUpload(): void {
    this.backupBusy.set('drive-up');
    this.backupError.set(null);
    this.backupMsg.set(null);
    this.api.driveBackup().subscribe({
      next: () => {
        this.backupBusy.set(null);
        this.backupMsg.set(this.translate.instant('cache.backup_drive_done'));
        this.loadBackupStatus();
      },
      error: (e) => {
        this.backupBusy.set(null);
        this.backupError.set(this.backupErrText(e));
      },
    });
  }

  driveRestore(): void {
    if (!confirm(this.translate.instant('cache.backup_restore_confirm'))) return;
    this.backupBusy.set('drive-down');
    this.backupError.set(null);
    this.api.driveRestore().subscribe({
      next: () => location.reload(),
      error: (e) => {
        this.backupBusy.set(null);
        this.backupError.set(this.backupErrText(e));
      },
    });
  }

  private backupErrText(e: unknown): string {
    const err = (e as { error?: { code?: string; message?: string } })?.error;
    if (err?.code === 'drive_scope_missing') return this.translate.instant('cache.backup_scope_missing');
    if (err?.code === 'drive_api_disabled') return this.translate.instant('cache.backup_api_disabled');
    if (err?.message) return err.message;
    return this.apiError.message(e, 'cache.error_load', { msg: '' });
  }


  refreshCache(): void {
    this.loadCacheData();
  }
}
