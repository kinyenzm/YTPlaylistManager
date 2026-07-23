import {
  Component,
  ChangeDetectionStrategy,
  signal,
  computed,
  inject,
  OnDestroy,
  DestroyRef,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { LowerCasePipe } from '@angular/common';
import { TranslateModule } from '@ngx-translate/core';
import { Subject, of } from 'rxjs';
import { debounceTime, distinctUntilChanged, switchMap, catchError } from 'rxjs/operators';
import { ApiService } from '../../services/api.service';
import { AuthService } from '../../services/auth.service';
import { Playlist, SongSearchResult } from '../../models/models';

type GroupedSong = SongSearchResult & { playlistTitles: string[] };

type PaletteItem =
  | { kind: 'playlist'; data: Playlist }
  | { kind: 'song'; data: GroupedSong };

@Component({
  selector: 'app-command-palette',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, LowerCasePipe, TranslateModule],
  templateUrl: './command-palette.html',
})
export class CommandPalette implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly auth = inject(AuthService);

  protected readonly open = signal(false);
  protected readonly rawQuery = signal('');
  protected readonly songs = signal<SongSearchResult[]>([]);
  protected readonly loading = signal(false);
  protected readonly activeIndex = signal(0);

  private allPlaylists: Playlist[] = [];
  private readonly query$ = new Subject<string>();
  private readonly keyListener: (e: KeyboardEvent) => void;

  protected readonly filteredPlaylists = computed(() => {
    const q = this.rawQuery().toLowerCase().trim();
    const list = this.allPlaylists.filter(p => !p.isArchived);
    if (!q) return list.slice(0, 5);
    return list
      .filter(p =>
        p.title.toLowerCase().includes(q) ||
        p.description?.toLowerCase().includes(q),
      )
      .slice(0, 6);
  });

  protected readonly groupedSongs = computed<GroupedSong[]>(() => {
    const map = new Map<string, GroupedSong>();
    for (const s of this.songs()) {
      const title = s.currentPlaylistTitle || s.originalPlaylistTitle;
      if (map.has(s.videoId)) {
        const entry = map.get(s.videoId)!;
        if (title && !entry.playlistTitles.includes(title)) {
          entry.playlistTitles.push(title);
        }
      } else {
        map.set(s.videoId, { ...s, playlistTitles: title ? [title] : [] });
      }
    }
    return [...map.values()];
  });

  protected readonly allItems = computed<PaletteItem[]>(() => [
    ...this.filteredPlaylists().map(p => ({ kind: 'playlist' as const, data: p })),
    ...this.groupedSongs().map(s => ({ kind: 'song' as const, data: s })),
  ]);

  constructor() {
    this.keyListener = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        e.preventDefault();
        this.open() ? this.close() : this.openPalette();
        return;
      }
      if (!this.open()) return;
      if (e.key === 'Escape') { this.close(); return; }
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        this.activeIndex.update(i => Math.min(i + 1, this.allItems().length - 1));
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        this.activeIndex.update(i => Math.max(i - 1, 0));
      }
      if (e.key === 'Enter') {
        e.preventDefault();
        this.selectActive();
      }
    };
    document.addEventListener('keydown', this.keyListener);

    // Búsqueda con debounce: nombre de canción / videoId parcial / canal
    this.query$.pipe(
      debounceTime(220),
      distinctUntilChanged(),
      switchMap(q => {
        const trimmed = q.trim();
        if (trimmed.length < 2 || !this.auth.connected()) {
          this.songs.set([]);
          this.loading.set(false);
          return of(null);
        }
        this.loading.set(true);
        // Si parece un videoId (11 chars alfanumérico o guión) buscar por ID, si no por nombre
        const looksLikeId = /^[A-Za-z0-9_-]{8,}$/.test(trimmed) && !trimmed.includes(' ');
        const query = looksLikeId
          ? { videoIdPartial: trimmed, searchScope: 'active' }
          : { songNameFuzzy: trimmed, searchScope: 'active' };
        return this.api.searchSongs(query).pipe(catchError(() => of([])));
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(results => {
      this.loading.set(false);
      if (results != null) this.songs.set((results as SongSearchResult[]).slice(0, 10));
    });
  }

  ngOnDestroy(): void {
    document.removeEventListener('keydown', this.keyListener);
  }

  private openPalette(): void {
    this.open.set(true);
    this.rawQuery.set('');
    this.songs.set([]);
    this.activeIndex.set(0);
    this.loading.set(false);
    // Sin sesión no se cargan listas (el palette abre pero queda vacío).
    if (this.auth.connected()) {
      this.api.listPlaylists(false, false).subscribe(ps => {
        this.allPlaylists = ps;
      });
    } else {
      this.allPlaylists = [];
    }
    setTimeout(() => (document.getElementById('cp-input') as HTMLInputElement | null)?.focus(), 40);
  }

  close(): void {
    this.open.set(false);
    this.rawQuery.set('');
    this.songs.set([]);
  }

  onQuery(q: string): void {
    this.rawQuery.set(q);
    this.activeIndex.set(0);
    this.query$.next(q);
  }

  onKey(e: KeyboardEvent): void {
    // Let global listener handle arrows/enter/esc; prevent default only for those
    if (['ArrowUp', 'ArrowDown', 'Enter'].includes(e.key)) e.preventDefault();
  }

  selectPlaylist(p: Playlist): void {
    this.close();
    this.router.navigate(['/organizar/lista', p.id]);
  }

  selectSong(s: SongSearchResult): void {
    this.close();
    this.router.navigate(['/organizar'], { queryParams: { q: s.videoId } });
  }

  private selectActive(): void {
    const item = this.allItems()[this.activeIndex()];
    if (!item) return;
    if (item.kind === 'playlist') this.selectPlaylist(item.data);
    else this.selectSong(item.data);
  }

  protected thumbUrl(videoId: string): string {
    return `https://i.ytimg.com/vi/${videoId}/default.jpg`;
  }
}
