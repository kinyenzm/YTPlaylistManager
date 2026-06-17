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
import { Subject, of } from 'rxjs';
import { debounceTime, distinctUntilChanged, switchMap, catchError } from 'rxjs/operators';
import { ApiService } from '../../services/api.service';
import { Playlist, SongSearchResult } from '../../models/models';

type PaletteItem =
  | { kind: 'playlist'; data: Playlist }
  | { kind: 'song'; data: SongSearchResult };

@Component({
  selector: 'app-command-palette',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    @if (open()) {
      <div class="cmd-palette__backdrop" (click)="close()">
        <div class="cmd-palette__panel" (click)="$event.stopPropagation()">

          <div class="cmd-palette__input-row">
            <i class="fa-solid fa-magnifying-glass cmd-palette__icon--search"></i>
            <input
              id="cp-input"
              class="cmd-palette__input"
              [value]="rawQuery()"
              (input)="onQuery($any($event.target).value)"
              (keydown)="onKey($event)"
              placeholder="Playlist, canción, canal o video ID…"
              autocomplete="off"
              spellcheck="false"
            />
            @if (loading()) {
              <i class="fa-solid fa-circle-notch fa-spin cmd-palette__icon--spin"></i>
            } @else if (rawQuery().length > 0) {
              <button class="cmd-palette__clear" (click)="onQuery('')" tabindex="-1">
                <i class="fa-solid fa-xmark"></i>
              </button>
            } @else {
              <span class="cmd-palette__kbd">Esc</span>
            }
          </div>

          <div class="cmd-palette__results">
            @if (allItems().length === 0 && rawQuery().trim().length > 0 && !loading()) {
              <div class="cmd-palette__empty">Sin resultados para «{{ rawQuery() }}»</div>
            }

            @if (filteredPlaylists().length > 0) {
              <div class="cmd-palette__section">Playlists</div>
              @for (p of filteredPlaylists(); track p.id; let i = $index) {
                <div
                  class="cmd-palette__item"
                  [class.cmd-palette__item--active]="activeIndex() === i"
                  (mouseenter)="activeIndex.set(i)"
                  (click)="selectPlaylist(p)">
                  <img [src]="p.thumbnailUrl" class="cmd-palette__thumb" onerror="this.style.display='none'" alt="" />
                  <div class="cmd-palette__item-body">
                    <span class="cmd-palette__item-title">{{ p.title }}</span>
                    <span class="cmd-palette__item-meta">{{ p.itemCount }} canciones</span>
                  </div>
                  <i class="fa-solid fa-list cmd-palette__item-kind"></i>
                </div>
              }
            }

            @if (songs().length > 0) {
              <div class="cmd-palette__section">Canciones</div>
              @for (s of songs(); track s.videoId; let i = $index) {
                <div
                  class="cmd-palette__item"
                  [class.cmd-palette__item--active]="activeIndex() === filteredPlaylists().length + i"
                  (mouseenter)="activeIndex.set(filteredPlaylists().length + i)"
                  (click)="selectSong(s)">
                  <img [src]="thumbUrl(s.videoId)" class="cmd-palette__thumb" onerror="this.style.display='none'" alt="" />
                  <div class="cmd-palette__item-body">
                    <span class="cmd-palette__item-title">{{ s.title }}</span>
                    <span class="cmd-palette__item-meta">{{ s.channelTitle }} · {{ s.currentPlaylistTitle || s.originalPlaylistTitle }}</span>
                  </div>
                  @if (s.appearsInCount > 1) {
                    <span class="cmd-palette__badge">×{{ s.appearsInCount }}</span>
                  }
                  <i class="fa-brands fa-youtube cmd-palette__item-kind"></i>
                </div>
              }
            }

            @if (rawQuery().trim().length === 0) {
              <div class="cmd-palette__empty">
                Escribe para buscar · <kbd class="cmd-palette__kbd">↑↓</kbd> navegar · <kbd class="cmd-palette__kbd">↵</kbd> abrir
              </div>
            }
          </div>

          <div class="cmd-palette__footer">
            <span><kbd class="cmd-palette__kbd">Ctrl K</kbd> abrir/cerrar</span>
            <span><kbd class="cmd-palette__kbd">↑↓</kbd> navegar</span>
            <span><kbd class="cmd-palette__kbd">↵</kbd> abrir</span>
            <span><kbd class="cmd-palette__kbd">Esc</kbd> cerrar</span>
          </div>
        </div>
      </div>
    }
  `,
})
export class CommandPalette implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

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

  protected readonly allItems = computed<PaletteItem[]>(() => [
    ...this.filteredPlaylists().map(p => ({ kind: 'playlist' as const, data: p })),
    ...this.songs().map(s => ({ kind: 'song' as const, data: s })),
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
        if (trimmed.length < 2) {
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
    this.api.listPlaylists(false, false).subscribe(ps => {
      this.allPlaylists = ps;
    });
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
    this.router.navigate(['/organizar'], { state: { q: s.videoId } });
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
