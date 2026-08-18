import { Component, ChangeDetectionStrategy, signal, inject, input, effect, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { TranslateModule } from '@ngx-translate/core';
import { catchError, finalize, forkJoin, of } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { ApiErrorService } from '../../services/api-error.service';
import { CacheStatus, PlaylistArchivedInfo, MergeReviewSummary, ActivityItem } from '../../models/models';
import { SkeletonList } from '../ui/skeleton-list';
import { SongHistory } from '../song-history/song-history';

type Tab = 'dashboard' | 'archived' | 'reviews' | 'activity' | 'history';

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
  protected readonly connected = this.auth.connected;

  cacheStatus = signal<CacheStatus | null>(null);
  archivedPlaylists = signal<PlaylistArchivedInfo[]>([]);
  mergeReviews = signal<MergeReviewSummary[]>([]);
  activityLog = signal<ActivityItem[]>([]);

  isLoading = signal(false);
  error = signal<string | null>(null);

  activeTab = signal<Tab>('dashboard');

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
  }

  exportCacheAsJson(): void {
    const data = {
      status: this.cacheStatus(),
      archived: this.archivedPlaylists(),
      reviews: this.mergeReviews(),
      exportedAt: new Date().toISOString(),
    };
    const json = JSON.stringify(data, null, 2);
    const blob = new Blob([json], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `cache-export-${new Date().getTime()}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  refreshCache(): void {
    this.loadCacheData();
  }
}
