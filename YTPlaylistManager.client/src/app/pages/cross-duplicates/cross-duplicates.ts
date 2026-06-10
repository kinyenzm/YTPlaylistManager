import {
  Component,
  ChangeDetectionStrategy,
  DestroyRef,
  signal,
  computed,
  effect,
  inject,
  input,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { debounceTime, Subject } from 'rxjs';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { PendingService } from '../../services/pending.service';
import {
  ClassifyResult,
  CrossDuplicate,
  CrossDuplicateReport,
  DuplicateReport,
  Playlist,
  PlaylistItem,
  SongSearchResult,
} from '../../models/models';

type Mode = 'repeated' | 'byList' | 'bySong';
interface SongRow {
  videoId: string;
  title: string;
}

@Component({
  selector: 'app-cross-duplicates',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FormsModule, TranslateModule],
  templateUrl: './cross-duplicates.html',
})
export class CrossDuplicates {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);
  private readonly titleSvc = inject(Title);
  private readonly destroyRef = inject(DestroyRef);

  // Ruta /organizar/lista/:id → abre directo en modo "por lista" (absorbe el
  // viejo detalle de playlist). Sin :id la página arranca en "repetidas".
  readonly id = input<string>();

  protected readonly mode = signal<Mode>('repeated');
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly allPlaylists = signal<Playlist[]>([]);

  // id → título (para mostrar "en qué listas está")
  protected readonly titleById = computed<Record<string, string>>(() => {
    const m: Record<string, string> = {};
    for (const p of this.allPlaylists()) m[p.id] = p.title;
    return m;
  });
  // videoId → ids de listas (modo "por lista", cargado en lote)
  protected readonly locMap = signal<Record<string, string[]>>({});

  // Ordenados: primero las canciones que están en más de una playlist.
  protected readonly listItemsSorted = computed(() => {
    const m = this.locMap();
    return [...this.listItems()].sort(
      (a, b) => (m[b.videoId]?.length ?? 0) - (m[a.videoId]?.length ?? 0),
    );
  });
  protected readonly resultsSorted = computed(() =>
    [...this.results()].sort((a, b) => (b.appearsInCount ?? 0) - (a.appearsInCount ?? 0)),
  );
  protected readonly groupsSorted = computed(() => {
    const r = this.report();
    return r ? [...r.groups].sort((a, b) => b.playlistCount - a.playlistCount) : [];
  });

  thumb(videoId: string): string {
    return `https://i.ytimg.com/vi/${videoId}/default.jpg`;
  }
  listsFor(videoId: string): string[] {
    const t = this.titleById();
    return (this.locMap()[videoId] ?? []).map((id) => t[id] ?? id);
  }
  titlesOf(ids: string[]): string[] {
    const t = this.titleById();
    return ids.map((id) => t[id] ?? id);
  }
  refsFor(ids: string[]): { id: string; title: string }[] {
    const t = this.titleById();
    return ids.map((id) => ({ id, title: t[id] ?? id }));
  }
  playlistIdsOf(g: CrossDuplicate): string[] {
    return g.playlists.map((p) => p.playlistId);
  }
  isUnavailable(title: string): boolean {
    const t = title?.trim().toLowerCase();
    return t === 'private video' || t === 'deleted video';
  }

  // Modo "repetidas"
  protected readonly report = signal<CrossDuplicateReport | null>(null);

  // Modo "por lista"
  protected readonly listId = signal<string>('');
  protected readonly listItems = signal<PlaylistItem[]>([]);
  protected readonly listTitle = computed(() => this.titleById()[this.listId()] ?? null);

  // Canciones de esa lista repetidas en otras (badge rojo del selector)
  protected readonly dupCounts = signal<Record<string, number>>({});

  // ── Herramientas de lista (portadas del viejo detalle de playlist) ──
  protected readonly duplicates = signal<DuplicateReport | null>(null);
  protected readonly classification = signal<ClassifyResult | null>(null);
  protected readonly loadingDup = signal(false);
  protected readonly cleaning = signal(false);
  protected readonly classifying = signal(false);
  protected readonly strategy = signal<'videoId' | 'normalizedTitle'>('videoId');
  protected readonly aiMode = signal<'genre' | 'mood' | 'decade'>('genre');
  protected readonly stagedMsg = signal<string | null>(null);
  protected readonly aiError = signal<string | null>(null);

  protected readonly classKeys = computed(() => {
    const c = this.classification();
    return c ? Object.keys(c.groups) : [];
  });

  // Similares (mismo título, distinto video) primero; exactas (mismo video) después.
  protected readonly dupGroupsSorted = computed(() => {
    const d = this.duplicates();
    if (!d) return [];
    const rank = (m: string) => (m === 'normalizedTitle' ? 0 : 1);
    return [...d.groups].sort((a, b) => rank(a.matchType) - rank(b.matchType));
  });

  // Modo "por canción" — filtrado en vivo (debounce, sin Enter), fusión de /buscar
  protected readonly nameInput = signal<string>('');
  protected readonly idInput = signal<string>('');
  protected readonly searchScope = signal<'all' | 'active' | 'archived'>('all');
  protected readonly results = signal<SongSearchResult[]>([]);
  protected readonly searching = signal(false);
  private readonly searchSubject = new Subject<void>();

  // Eliminaciones preparadas por tarjeta: videoId → ids de listas marcadas para quitar.
  protected readonly stagedRemovals = signal<Record<string, ReadonlySet<string>>>({});

  // Editor de asignación (compartido, modal) — solo multi-selección.
  protected readonly editingVideoId = signal<string | null>(null);
  protected readonly editingTitle = signal<string>('');
  protected readonly selection = signal<ReadonlySet<string>>(new Set());
  protected readonly applying = signal(false);
  protected readonly editorLoading = signal(false);
  // Listas ordenadas para el modal: primero donde ya está, luego el resto (alfabético).
  protected readonly editorPlaylists = signal<Playlist[]>([]);

  private readonly pendingSvc = inject(PendingService);

  constructor() {
    this.loadPlaylists();
    this.loadDupCounts();
    this.pendingSvc.refresh();

    this.searchSubject
      .pipe(debounceTime(400))
      .subscribe(() => this.search());

    // Refrescar el modo activo cuando el panel global sube/descarta cambios.
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

  // ── Modo repetidas ──
  scan(refresh = false): void {
    this.loading.set(true);
    this.error.set(null);
    this.report.set(null);
    this.api.crossDuplicates(refresh).subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
        this.loadPlaylists();
      },
      error: (e) => {
        this.error.set(
          e?.status === 403
            ? this.translate.instant('common.youtube_quota_exhausted')
            : this.translate.instant('cross.error_scan'),
        );
        this.loading.set(false);
        console.error(e);
      },
    });
  }

  // ── Modo por lista ──
  pickList(id: string): void {
    this.listId.set(id);
    this.duplicates.set(null);
    this.classification.set(null);
    this.stagedMsg.set(null);
    this.aiError.set(null);
    this.closeEditor();
    if (!id) {
      this.listItems.set([]);
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    this.api.listItems(id, true).subscribe({   // solo caché: nunca lee de YouTube
      next: (items) => {
        this.listItems.set(items);
        this.loading.set(false);
        const ids = items.map((i) => i.videoId).filter(Boolean);
        if (ids.length) {
          this.api.songLocationsBatch(ids).subscribe({
            next: (m) => this.locMap.set(m),
            error: (e) => console.error(e),
          });
        }
      },
      error: (e) => {
        this.error.set(this.translate.instant('cross.error_scan'));
        this.loading.set(false);
        console.error(e);
      },
    });
  }

  // ── Herramientas de lista (portadas del detalle): repetidas internas + IA ──
  loadDuplicates(): void {
    if (!this.listId()) return;
    this.loadingDup.set(true);
    this.api.findDuplicates(this.listId()).subscribe({
      next: (r) => {
        this.duplicates.set(r);
        this.loadingDup.set(false);
        this.api.refreshQuota();
        this.pickList(this.listId());
      },
      error: () => this.loadingDup.set(false),
    });
  }

  cleanDuplicates(): void {
    if (!this.listId()) return;
    if (!confirm(this.translate.instant('detail.confirm_remove'))) return;
    this.cleaning.set(true);
    this.api.removeDuplicates(this.listId(), this.strategy()).subscribe({
      next: (r) => {
        alert(this.translate.instant('detail.alert_removed', { removed: r.removed, kept: r.kept }));
        this.cleaning.set(false);
        this.pickList(this.listId());
        this.loadDuplicates();
      },
      error: () => this.cleaning.set(false),
    });
  }

  private stageRemoval(ids: string[], songTitle: string): void {
    if (ids.length === 0) return;
    const msg = this.translate.instant('detail.dup_confirm', { n: ids.length, title: songTitle });
    if (!confirm(msg)) return;
    this.api.removeItemsFromPlaylist(this.listId(), ids).subscribe((r) => {
      this.api.refreshQuota();
      this.stagedMsg.set(this.translate.instant('detail.dup_staged', { n: r.staged }));
      // Poda local de la vista de repetidas: NO se re-lee de YouTube (la remoción
      // todavía no está allá; releer restauraría la caché y "desharía" lo quitado).
      const dup = this.duplicates();
      if (dup) {
        const removed = new Set(ids);
        const groups = dup.groups
          .map((g) => ({ ...g, items: g.items.filter((i) => !removed.has(i.playlistItemId)) }))
          .filter((g) => g.items.length > 1);
        this.duplicates.set({
          ...dup,
          groups,
          duplicateCount: groups.reduce((acc, g) => acc + g.items.length - 1, 0),
          totalItems: dup.totalItems - removed.size,
        });
      }
      this.pickList(this.listId());
      this.pendingSvc.refresh();
    });
  }

  removeCopy(it: { playlistItemId: string; title: string }): void {
    this.stageRemoval([it.playlistItemId], it.title);
  }

  keepThis(items: { playlistItemId: string; title: string }[], keepId: string): void {
    const toRemove = items.filter((i) => i.playlistItemId !== keepId);
    this.stageRemoval(toRemove.map((i) => i.playlistItemId), items[0]?.title ?? '');
  }

  classify(): void {
    if (!this.listId()) return;
    this.classifying.set(true);
    this.aiError.set(null);
    this.api.classify(this.listId(), this.aiMode()).subscribe({
      next: (r) => {
        this.classification.set(r);
        this.classifying.set(false);
      },
      error: (e) => {
        this.classifying.set(false);
        this.aiError.set(
          e?.status === 503
            ? this.translate.instant('detail.ai_config_error')
            : this.translate.instant('detail.ai_generic_error'),
        );
      },
    });
  }

  // ── Modo por canción (fusión de /buscar): filtros en vivo ──
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
    }).subscribe({
      next: (r) => {
        this.results.set(r);
        this.searching.set(false);
      },
      error: (e) => {
        this.error.set(this.translate.instant('cross.error_scan'));
        this.searching.set(false);
        console.error(e);
      },
    });
  }

  // ── Badges editables: quitar de una lista sin abrir el modal ──
  removedSet(videoId: string): ReadonlySet<string> {
    return this.stagedRemovals()[videoId] ?? new Set();
  }

  toggleRemoval(videoId: string, playlistId: string): void {
    const all = { ...this.stagedRemovals() };
    const cur = new Set(all[videoId] ?? []);
    if (cur.has(playlistId)) cur.delete(playlistId);
    else cur.add(playlistId);
    if (cur.size === 0) delete all[videoId];
    else all[videoId] = cur;
    this.stagedRemovals.set(all);
  }

  discardCard(videoId: string): void {
    const all = { ...this.stagedRemovals() };
    delete all[videoId];
    this.stagedRemovals.set(all);
  }

  // Guardar sin modal: desired = listas actuales − marcadas. Entra al flujo staged
  // existente (assignSong → panel global de pendientes).
  saveCard(videoId: string, title: string, currentIds: string[]): void {
    const removed = this.removedSet(videoId);
    const desired = currentIds.filter((id) => !removed.has(id));
    this.applying.set(true);
    this.error.set(null);
    this.api
      .assignSong({ videoId, title, channelTitle: null, thumbnailUrl: null, desiredPlaylistIds: desired })
      .subscribe({
        next: () => {
          this.applying.set(false);
          this.discardCard(videoId);
          this.pendingSvc.refresh();
          this.refreshCurrentMode();
          this.loadDupCounts();
        },
        error: (e) => {
          this.error.set(this.translate.instant('cross.assign_error'));
          this.applying.set(false);
          console.error(e);
        },
      });
  }

  // ── Editor de asignación (modal, solo para AGREGAR a listas nuevas) ──
  openEditor(row: SongRow): void {
    this.editingVideoId.set(row.videoId);
    this.editingTitle.set(row.title);
    this.editorLoading.set(true);
    this.api.songLocations(row.videoId).subscribe({
      next: (locs) => {
        const sel = new Set(locs);
        this.selection.set(sel);
        // Primero las listas donde ya está; el resto alfabético (orden del backend).
        this.editorPlaylists.set(
          [...this.allPlaylists()].sort(
            (a, b) => (sel.has(b.id) ? 1 : 0) - (sel.has(a.id) ? 1 : 0),
          ),
        );
        this.editorLoading.set(false);
      },
      error: (e) => {
        this.selection.set(new Set());
        this.editorPlaylists.set([...this.allPlaylists()]);
        this.editorLoading.set(false);
        console.error(e);
      },
    });
  }

  closeEditor(): void {
    this.editingVideoId.set(null);
  }

  isChecked(pid: string): boolean {
    return this.selection().has(pid);
  }

  toggle(pid: string): void {
    const n = new Set(this.selection());
    if (n.has(pid)) n.delete(pid);
    else n.add(pid);
    this.selection.set(n);
  }

  apply(): void {
    const vid = this.editingVideoId();
    if (!vid) return;
    this.applying.set(true);
    this.error.set(null);
    this.api
      .assignSong({
        videoId: vid,
        title: this.editingTitle(),
        channelTitle: null,
        thumbnailUrl: null,
        desiredPlaylistIds: [...this.selection()],
      })
      .subscribe({
        next: () => {
          this.applying.set(false);
          this.closeEditor();
          this.pendingSvc.refresh();
          this.refreshCurrentMode();
          this.loadDupCounts();
        },
        error: (e) => {
          this.error.set(this.translate.instant('cross.assign_error'));
          this.applying.set(false);
          console.error(e);
        },
      });
  }

  private refreshCurrentMode(): void {
    const m = this.mode();
    if (m === 'repeated' && this.report()) this.scan(false);
    else if (m === 'byList' && this.listId()) this.pickList(this.listId());
    else if (m === 'bySong' && this.results().length) this.search();
  }

}
