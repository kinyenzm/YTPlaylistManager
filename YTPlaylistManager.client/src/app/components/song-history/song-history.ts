import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Subject, debounceTime, finalize } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { ApiErrorService } from '../../services/api-error.service';
import { SongMovementLog, SongSearchResult } from '../../models/models';
import { looksLikeVideoId, thumbUrl } from '../../utils/youtube.utils';
import { SkeletonList } from '../ui/skeleton-list';
import { EmptyState } from '../ui/empty-state';

/**
 * Historial de una canción: se busca por nombre o id sobre la caché (0 cuota)
 * y se abre la línea de tiempo de la elegida.
 */
@Component({
  selector: 'app-song-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, TranslatePipe, SkeletonList, EmptyState],
  templateUrl: './song-history.html',
})
export class SongHistory {
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);

  protected readonly query = signal('');
  protected readonly results = signal<SongSearchResult[]>([]);
  protected readonly searching = signal(false);
  protected readonly searched = signal(false);
  protected readonly history = signal<SongMovementLog | null>(null);
  protected readonly loadingHistory = signal(false);
  protected readonly error = signal<string | null>(null);

  private readonly typed = new Subject<void>();

  constructor() {
    this.typed.pipe(debounceTime(400)).subscribe(() => this.search());
  }

  protected thumb(videoId: string): string {
    return thumbUrl(videoId);
  }

  onQueryChange(value: string): void {
    this.query.set(value);
    this.typed.next();
  }

  search(): void {
    const q = this.query().trim();
    if (!q) {
      this.results.set([]);
      this.searched.set(false);
      return;
    }
    this.searching.set(true);
    this.error.set(null);
    this.api.searchSongs(
      looksLikeVideoId(q)
        ? { videoIdPartial: q, searchScope: 'all' }
        : { songNameFuzzy: q, searchScope: 'all' },
    )
      .pipe(finalize(() => { this.searching.set(false); this.searched.set(true); }))
      .subscribe({
        // Una fila por canción: la búsqueda devuelve una por playlist donde aparece.
        next: (r) => this.results.set([...new Map(r.map((s) => [s.videoId, s])).values()]),
        error: (e) => this.error.set(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  open(videoId: string): void {
    this.loadingHistory.set(true);
    this.error.set(null);
    this.api.getSongHistory(videoId)
      .pipe(finalize(() => this.loadingHistory.set(false)))
      .subscribe({
        next: (h) => this.history.set(h),
        error: (e) => this.error.set(this.apiError.message(e, 'cache.error_history', { msg: (e as Error)?.message })),
      });
  }

  clear(): void {
    this.history.set(null);
  }
}
