import { Component, ChangeDetectionStrategy, signal, computed, inject, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { debounceTime, Subject } from 'rxjs';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { SongSearchQuery, SongSearchResult, CacheStatus } from '../../models/models';

@Component({
  selector: 'app-song-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TranslateModule],
  templateUrl: './song-search.html',
})
export class SongSearch implements OnInit {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  videoIdInput = signal<string>('');
  songNameInput = signal<string>('');
  searchScope = signal<'all' | 'active' | 'archived'>('all');

  results = signal<SongSearchResult[]>([]);
  cacheStatus = signal<CacheStatus | null>(null);

  isLoading = signal(false);
  error = signal<string | null>(null);

  hasQuery = computed(() =>
    this.videoIdInput().trim().length > 0 || this.songNameInput().trim().length > 3
  );

  canSearch = computed(() => this.hasQuery() && !this.isLoading());

  private searchSubject = new Subject<void>();

  ngOnInit(): void {
    this.searchSubject
      .pipe(debounceTime(500))
      .subscribe(() => this.performSearch());

    this.loadCacheStatus();
  }

  performSearch(): void {
    if (!this.hasQuery()) {
      this.results.set([]);
      return;
    }

    this.isLoading.set(true);
    this.error.set(null);

    const query: SongSearchQuery = {
      videoIdPartial: this.videoIdInput().trim() || null,
      songNameFuzzy: this.songNameInput().trim() || null,
      searchScope: this.searchScope(),
    };

    this.api.searchSongs(query).subscribe({
      next: (data) => {
        this.results.set(data);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.error.set(this.translate.instant('search.error_search', { msg: err.message }));
        this.isLoading.set(false);
      },
    });
  }

  loadCacheStatus(): void {
    this.api.getCacheStatus().subscribe({
      next: (status) => this.cacheStatus.set(status),
      error: (err) => console.error('Error loading cache status:', err),
    });
  }

  clearSearch(): void {
    this.videoIdInput.set('');
    this.songNameInput.set('');
    this.results.set([]);
    this.error.set(null);
  }

  copyToClipboard(text: string): void {
    navigator.clipboard.writeText(text);
  }

  viewSongHistory(videoId: string): void {
    console.log('View history for:', videoId);
  }

  onVideoIdChange(): void {
    this.searchSubject.next();
  }

  onSongNameChange(): void {
    this.searchSubject.next();
  }
}
