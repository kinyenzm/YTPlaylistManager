import {
  Component,
  ChangeDetectionStrategy,
  DestroyRef,
  HostListener,
  signal,
  computed,
  effect,
  inject,
  input,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { debounceTime, delay, finalize, Subject } from 'rxjs';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { ApiErrorService } from '../../services/api-error.service';
import { AuthService } from '../../services/auth.service';
import { DraftsService } from '../../services/drafts.service';
import { PendingService } from '../../services/pending.service';
import { CrossDuplicateReport, Playlist, PlaylistItem, SongSearchResult } from '../../models/models';
import { looksLikeVideoId } from '../../utils/youtube.utils';
import { AssignModal } from '../../components/assign-modal/assign-modal';
import { ListTools } from '../../components/list-tools/list-tools';
import { RecoverTab } from '../../components/recover-tab/recover-tab';
import { SongCard } from '../../components/song-card/song-card';
import { EmptyState } from '../../components/ui/empty-state';
import { SkeletonList } from '../../components/ui/skeleton-list';

type Mode = 'repeated' | 'byList' | 'bySong' | 'recover';

/**
 * Organizador: elige el modo, carga sus datos y coordina los borradores.
 * Las herramientas de lista, la recuperación y el editor de asignación viven
 * en sus propios componentes.
 */
@Component({
  selector: 'app-cross-duplicates',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink, FormsModule, TranslateModule,
    AssignModal, ListTools, RecoverTab, SongCard,
    EmptyState, SkeletonList,
  ],
  providers: [DraftsService],
  templateUrl: './cross-duplicates.html',
})
export class CrossDuplicates {
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);
  private readonly translate = inject(TranslateService);
  private readonly titleSvc = inject(Title);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly pendingSvc = inject(PendingService);
  protected readonly drafts = inject(DraftsService);
  protected readonly connected = this.auth.connected;

  private readonly listTools = viewChild(ListTools);
  private readonly recoverTab = viewChild(RecoverTab);

  // Ruta /organizar/lista/:id → abre directo en modo "por lista".
  readonly id = input<string>();
  // Query param ?q=<videoId> → modo "por canción" pre-buscado (command palette).
  readonly q = input<string>();

  protected readonly mode = signal<Mode>('repeated');
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly allPlaylists = signal<Playlist[]>([]);

  // id → título, para pintar los tags "aparece en".
  protected readonly titleById = computed<Record<string, string>>(() => {
    const m: Record<string, string> = {};
    for (const p of this.allPlaylists()) m[p.id] = p.title;
    return m;
  });
  // videoId → ids de listas (modo "por lista", cargado en lote)
  protected readonly locMap = signal<Record<string, string[]>>({});

  // Modo "repetidas"
  protected readonly report = signal<CrossDuplicateReport | null>(null);
  protected readonly groupsSorted = computed(() => {
    const r = this.report();
    return r ? [...r.groups].sort((a, b) => b.playlistCount - a.playlistCount) : [];
  });

  // Modo "por lista"
  protected readonly listId = signal<string>('');
  protected readonly listItems = signal<PlaylistItem[]>([]);
  protected readonly loadingItems = signal(false);
  protected readonly listTitle = computed(() => this.titleById()[this.listId()] ?? null);
  // Canciones de esa lista repetidas en otras (badge rojo del selector)
  protected readonly dupCounts = signal<Record<string, number>>({});
  // Copias del mismo video dentro de la lista abierta (videoId → cuántas veces).
  protected readonly copiesByVideo = computed<Record<string, number>>(() => {
    const m: Record<string, number> = {};
    for (const it of this.listItems()) {
      if (it.videoId) m[it.videoId] = (m[it.videoId] ?? 0) + 1;
    }
    return m;
  });
  // Primero las repetidas dentro de la misma lista; después las que están en
  // más playlists; el resto conserva el orden de la lista.
  protected readonly listItemsSorted = computed(() => {
    const copies = this.copiesByVideo();
    const m = this.locMap();
    return [...this.listItems()].sort((a, b) => {
      const byCopies = (copies[b.videoId] ?? 0) - (copies[a.videoId] ?? 0);
      if (byCopies !== 0) return byCopies;
      return (m[b.videoId]?.length ?? 0) - (m[a.videoId]?.length ?? 0);
    });
  });

  // Modo "por canción" — filtrado en vivo (debounce, sin Enter)
  protected readonly nameInput = signal<string>('');
  protected readonly idInput = signal<string>('');
  protected readonly searchScope = signal<'all' | 'active' | 'archived'>('all');
  protected readonly results = signal<SongSearchResult[]>([]);
  protected readonly searching = signal(false);
  private readonly searchSubject = new Subject<void>();
  protected readonly resultsDeduped = computed(() => {
    const seen = new Set<string>();
    return [...this.results()]
      .sort((a, b) => (b.appearsInCount ?? 0) - (a.appearsInCount ?? 0))
      .filter((r) => {
        if (seen.has(r.videoId)) return false;
        seen.add(r.videoId);
        return true;
      });
  });

  // Editor de asignación (modal compartido por los tres modos con tarjetas).
  protected readonly editingVideoId = signal<string | null>(null);
  protected readonly editingTitle = signal<string>('');


  constructor() {
    // Cargas iniciales solo con sesión; al desconectar se limpia todo
    // — regla: sin sesión no se muestra ni caché.
    effect(() => {
      if (this.connected()) {
        untracked(() => {
          this.loadPlaylists();
          this.loadDupCounts();
          this.pendingSvc.refresh();
        });
      } else {
        untracked(() => this.clearAllData());
      }
    });

    this.searchSubject.pipe(debounceTime(400)).subscribe(() => this.search());

    // Refrescar el modo activo cuando el panel global sube o descarta cambios.
    effect(() => {
      if (this.pendingSvc.mutations() === 0) return;
      untracked(() => {
        this.refreshCurrentMode();
        this.loadDupCounts();
      });
    });

    // Deep-link /organizar/lista/:id → modo "por lista" con esa lista elegida.
    effect(() => {
      const pid = this.id();
      if (!pid) return;
      untracked(() => {
        this.mode.set('byList');
        this.pickList(pid);
      });
    });

    // Deep-link desde el command palette → modo "por canción" pre-buscado.
    // Funciona estando ya en /organizar porque el query param cambia la URL.
    effect(() => {
      const navQ = this.q();
      if (!navQ) return;
      untracked(() => {
        if (looksLikeVideoId(navQ)) this.idInput.set(navQ);
        else this.nameInput.set(navQ);
        this.mode.set('bySong');
        setTimeout(() => {
          this.search();
          // Limpia el param para que el botón Atrás no lo reactive.
          this.router.navigate([], { queryParams: { q: null }, queryParamsHandling: 'merge', replaceUrl: true });
        }, 0);
      });
    });

    // Título del documento: "{lista} — {app}" cuando hay lista elegida.
    effect(() => {
      const t = this.listTitle();
      const app = this.translate.instant('app.title');
      this.titleSvc.setTitle(t && this.mode() === 'byList' ? `${t} — ${app}` : app);
    });
    this.destroyRef.onDestroy(() =>
      this.titleSvc.setTitle(this.translate.instant('app.title')));
  }

  setMode(m: Mode): void {
    this.mode.set(m);
    this.closeEditor();
    this.error.set(null);
    if (m === 'recover') setTimeout(() => this.recoverTab()?.ensureLoaded(), 0);
  }

  private loadPlaylists(): void {
    this.api.listPlaylists().subscribe({
      next: (p) => this.allPlaylists.set(p.filter((x) => !x.isArchived)),
      error: (e) => console.error(e),
    });
  }

  private loadDupCounts(): void {
    this.api.duplicateCounts().subscribe({
      next: (c) => this.dupCounts.set(c),
      error: (e) => console.error(e),
    });
  }

  optionLabel(pl: Playlist): string {
    const base = `${pl.title} (${pl.itemCount})`;
    const n = this.dupCounts()[pl.id] ?? 0;
    return n > 0 ? `${base} — ${this.translate.instant('cross.dups_in_list', { n })}` : base;
  }

  playlistIdsOf(g: { playlists: { playlistId: string }[] }): string[] {
    return g.playlists.map((p) => p.playlistId);
  }

  // ── Modo repetidas ──
  scan(refresh = false): void {
    this.loading.set(true);
    this.error.set(null);
    this.report.set(null);
    this.api.crossDuplicates(refresh)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (r) => {
          this.report.set(r);
          this.loadPlaylists();
        },
        error: (e) => this.error.set(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  // ── Modo por lista ──
  pickList(id: string): void {
    this.listId.set(id);
    this.listTools()?.reset();
    this.closeEditor();
    this.listItems.set([]);
    if (!id) return;
    this.loadingItems.set(true);
    this.error.set(null);
    this.api.listItems(id, true)
      .pipe(delay(0), finalize(() => this.loadingItems.set(false)))   // delay(0) rompe la cadena síncrona del caché para que Angular pinte el skeleton
      .subscribe({
        next: (items) => {
          this.listItems.set(items);
          const ids = items.map((i) => i.videoId).filter(Boolean);
          if (ids.length) {
            this.api.songLocationsBatch(ids).subscribe({
              next: (m) => this.locMap.set(m),
              error: (e) => console.error(e),
            });
          }
        },
        error: (e) => this.error.set(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  reloadList(): void {
    this.pickList(this.listId());
  }

  // ── Modo por canción ──
  onFilterChange(): void {
    this.searchSubject.next();
  }

  search(): void {
    const name = this.nameInput().trim();
    const id = this.idInput().trim();
    if (!name && !id) {
      this.results.set([]);
      return;
    }
    this.searching.set(true);
    this.error.set(null);
    this.api.searchSongs({
      videoIdPartial: id || null,
      songNameFuzzy: name || null,
      searchScope: this.searchScope(),
    }).pipe(finalize(() => this.searching.set(false))).subscribe({
      next: (r) => this.results.set(r),
      error: (e) => this.error.set(this.apiError.message(e, 'cross.error_scan')),
    });
  }

  // ── Borradores ──
  async saveAll(): Promise<void> {
    if (this.drafts.count() === 0 || this.drafts.saving()) return;
    if (!confirm(this.translate.instant('cross.draft_save_confirm', { n: this.drafts.count() }))) return;
    // Solo encola: la subida se dispara desde el chip de pendientes.
    await this.saveDrafts();
  }

  discardAllDrafts(): void {
    if (!confirm(this.translate.instant('cross.draft_discard_confirm', { n: this.drafts.count() }))) return;
    this.drafts.discardAll();
  }

  private async saveDrafts(): Promise<boolean> {
    const ok = await this.drafts.saveAll();
    if (this.drafts.error()) this.error.set(this.drafts.error());
    this.refreshCurrentMode();
    this.loadDupCounts();
    return ok;
  }

  // Guard de salida: con borradores pendientes pregunta una vez; OK = guarda y sale.
  async canLeave(): Promise<boolean> {
    if (this.drafts.count() === 0) return true;
    if (!confirm(this.translate.instant('cross.draft_leave_confirm', { n: this.drafts.count() }))) return false;
    return await this.saveDrafts();
  }

  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(e: BeforeUnloadEvent): void {
    if (this.drafts.count() > 0) e.preventDefault();
  }

  // ── Editor de asignación ──
  openEditor(videoId: string, title: string): void {
    this.editingVideoId.set(videoId);
    this.editingTitle.set(title);
  }

  closeEditor(): void {
    this.editingVideoId.set(null);
  }

  private refreshCurrentMode(): void {
    const m = this.mode();
    if (m === 'repeated' && this.report()) this.scan(false);
    else if (m === 'byList' && this.listId()) this.pickList(this.listId());
    else if (m === 'bySong' && this.results().length) this.search();
    else if (m === 'recover') this.recoverTab()?.load();
  }

  // Al perder la sesión: sin datos en pantalla (ni de caché) y sin borradores.
  private clearAllData(): void {
    this.report.set(null);
    this.allPlaylists.set([]);
    this.dupCounts.set({});
    this.listId.set('');
    this.listItems.set([]);
    this.locMap.set({});
    this.listTools()?.reset();
    this.results.set([]);
    this.nameInput.set('');
    this.idInput.set('');
    this.recoverTab()?.clear();
    this.drafts.discardAll();
    this.closeEditor();
    this.error.set(null);
  }
}
