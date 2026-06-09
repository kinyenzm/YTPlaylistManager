import { Component, ChangeDetectionStrategy, signal, inject, input, effect, OnInit } from '@angular/core';
import { DatePipe } from '@angular/common';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { CacheStatus, PlaylistArchivedInfo, MergeReviewSummary, SongMovementLog, ActivityItem } from '../../models/models';

@Component({
  selector: 'app-cache-explorer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, TranslateModule],
  templateUrl: './cache-explorer.html',
})
export class CacheExplorer implements OnInit {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  cacheStatus = signal<CacheStatus | null>(null);
  archivedPlaylists = signal<PlaylistArchivedInfo[]>([]);
  mergeReviews = signal<MergeReviewSummary[]>([]);
  selectedSongHistory = signal<SongMovementLog | null>(null);
  activityLog = signal<ActivityItem[]>([]);

  isLoading = signal(false);
  error = signal<string | null>(null);

  activeTab = signal<'dashboard' | 'archived' | 'reviews' | 'activity'>('dashboard');

  // Deep-link: el "Ver más" del panel flotante navega con ?tab=activity.
  readonly tab = input<string>();

  constructor() {
    effect(() => {
      if (this.tab() === 'activity') this.activeTab.set('activity');
    });
  }

  ngOnInit(): void {
    this.loadCacheData();
    this.loadActivity();
  }

  loadActivity(): void {
    this.api.getActivityLog(1000).subscribe({
      next: (log) => this.activityLog.set(log),
      error: (err) => console.error('Error loading activity log:', err),
    });
  }

  loadCacheData(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.api.getCacheStatus().subscribe({
      next: (status) => {
        this.cacheStatus.set(status);
        this.loadArchivedPlaylists();
      },
      error: (err) => {
        this.error.set(this.translate.instant('cache.error_load', { msg: err.message }));
        this.isLoading.set(false);
      },
    });
  }

  loadArchivedPlaylists(): void {
    this.api.getArchivedPlaylists().subscribe({
      next: (archived) => {
        this.archivedPlaylists.set(archived);
        this.loadMergeReviews();
      },
      error: (err) => {
        console.error('Error loading archived playlists:', err);
        this.loadMergeReviews();
      },
    });
  }

  loadMergeReviews(): void {
    this.api.getMergeReviews().subscribe({
      next: (reviews) => {
        this.mergeReviews.set(reviews);
        this.isLoading.set(false);
      },
      error: (err) => {
        console.error('Error loading merge reviews:', err);
        this.isLoading.set(false);
      },
    });
  }

  viewSongHistory(videoId: string): void {
    this.api.getSongHistory(videoId).subscribe({
      next: (history) => {
        this.selectedSongHistory.set(history);
      },
      error: (err) => {
        this.error.set(this.translate.instant('cache.error_history', { msg: err.message }));
      },
    });
  }

  closeHistoryModal(): void {
    this.selectedSongHistory.set(null);
  }

  switchTab(tab: 'dashboard' | 'archived' | 'reviews' | 'activity'): void {
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
