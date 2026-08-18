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
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { debounceTime, delay, finalize, firstValueFrom, Subject } from 'rxjs';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { ApiErrorService } from '../../services/api-error.service';
import { AuthService } from '../../services/auth.service';
import { PendingService } from '../../services/pending.service';
import { looksLikeVideoId, thumbUrl } from '../../utils/youtube.utils';
import {
  ClassifyResult,
  CrossDuplicate,
  CrossDuplicateReport,
  DuplicateReport,
  Playlist,
  PlaylistItem,
  RecoverableSong,
  SongSearchResult,
} from '../../models/models';

type Mode = 'repeated' | 'byList' | 'bySong' | 'recover';
interface SongRow {
  videoId: string;
  title: string;
}

// Borrador local de una canción: baseline = listas donde está según caché al momento
// de editar; desired = listas donde debe quedar. Nada toca la API hasta "Guardar todo".
interface SongDraft {
  title: string;
  baseline: string[];
  desired: string[];
}

@Component({
  selector: 'app-cross-duplicates',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FormsModule, TranslateModule],
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
  protected readonly connected = this.auth.connected;

  // Ruta /organizar/lista/:id → abre directo en modo "por lista".
  // Sin :id la página arranca en "repetidas".
  readonly id = input<string>();
  // Query param ?q=<videoId> → modo "por canción" pre-buscado (desde command palette, funciona estando ya en la ruta).
  readonly q = input<string>();

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
  protected readonly resultsDeduped = computed(() => {
    const seen = new Set<string>();
    return this.resultsSorted().filter(r => {
      if (seen.has(r.videoId)) return false;
      seen.add(r.videoId);
      return true;
    });
  });
  protected readonly groupsSorted = computed(() => {
    const r = this.report();
    return r ? [...r.groups].sort((a, b) => b.playlistCount - a.playlistCount) : [];
  });

  thumb(videoId: string): string {
    return thumbUrl(videoId);
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

  // ── Herramientas de lista ──
  protected readonly duplicates = signal<DuplicateReport | null>(null);
  protected readonly classification = signal<ClassifyResult | null>(null);
  protected readonly loadingItems = signal(false);
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

  // Modo "recuperar" — huérfanas: conocidas por la app pero fuera de toda playlist actual
  protected readonly recoverable = signal<RecoverableSong[]>([]);
  protected readonly loadingRecover = signal(false);
  protected readonly recoverLoaded = signal(false);
  // Selección múltiple + destino (lista existente o nueva) para encolar la recuperación.
  protected readonly recoverSel = signal<ReadonlySet<string>>(new Set());
  protected readonly recoverTargetId = signal<string>('');   // '' = crear lista nueva
  protected readonly recoverNewTitle = signal<string>('');
  protected readonly stagingRecover = signal(false);
  // Filtro por lista de origen (lastKnownPlaylist): permite recuperar una lista borrada entera.
  protected readonly recoverListFilter = signal<string>('');
  protected readonly recoverLists = computed(() => {
    const counts = new Map<string, number>();
    for (const r of this.recoverable()) {
      counts.set(r.lastKnownPlaylist, (counts.get(r.lastKnownPlaylist) ?? 0) + 1);
    }
    return [...counts.entries()]
      .map(([name, n]) => ({ name, n }))
      .sort((a, b) => b.n - a.n);
  });
  protected readonly recoverableFiltered = computed(() => {
    const f = this.recoverListFilter();
    return f ? this.recoverable().filter((r) => r.lastKnownPlaylist === f) : this.recoverable();
  });
  protected readonly allRecoverSelected = computed(() => {
    const list = this.recoverableFiltered();
    if (list.length === 0) return false;
    const sel = this.recoverSel();
    return list.every((r) => sel.has(r.videoId));
  });

  // Modo "por canción" — filtrado en vivo (debounce, sin Enter)
  protected readonly nameInput = signal<string>('');
  protected readonly idInput = signal<string>('');
  protected readonly searchScope = signal<'all' | 'active' | 'archived'>('all');
  protected readonly results = signal<SongSearchResult[]>([]);
  protected readonly searching = signal(false);
  private readonly searchSubject = new Subject<void>();

  // Borradores acumulados por canción (videoId → SongDraft). Se guardan todos juntos
  // con la barra global; ninguna edición dispara API hasta entonces.
  protected readonly drafts = signal<Record<string, SongDraft>>({});
  protected readonly draftCount = computed(() => Object.keys(this.drafts()).length);
  protected readonly savingAll = signal(false);

  // Editor de asignación (compartido, modal) — solo multi-selección.
  protected readonly editingVideoId = signal<string | null>(null);
  protected readonly editingTitle = signal<string>('');
  protected readonly selection = signal<ReadonlySet<string>>(new Set());
  // Estado original del servidor para el apply del modal (baseline del draft).
  private readonly editorBaseline = signal<string[]>([]);
  protected readonly editorLoading = signal(false);
  // Listas ordenadas para el modal: primero donde ya está, luego el resto (alfabético).
  protected readonly editorPlaylists = signal<Playlist[]>([]);

  private readonly pendingSvc = inject(PendingService);

  constructor() {
    // Cargas iniciales solo con sesión; al desconectar (401/logout) se limpia todo
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

    // Deep-link desde el command palette → modo "por canción" pre-buscado (?q=videoId).
    // Funciona incluso cuando ya se está en /organizar porque el query param cambia la URL.
    effect(() => {
      const navQ = this.q();
      if (!navQ) return;
      untracked(() => {
        if (looksLikeVideoId(navQ)) {
          this.idInput.set(navQ);
        } else {
          this.nameInput.set(navQ);
        }
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
    if (m === 'recover' && !this.recoverLoaded()) this.loadRecoverable();
  }

  loadRecoverable(): void {
    this.loadingRecover.set(true);
    this.error.set(null);
    this.recoverSel.set(new Set());
    this.api.recoverableSongs()
      .pipe(delay(0), finalize(() => this.loadingRecover.set(false)))   // delay(0): deja pintar el skeleton
      .subscribe({
        next: (r) => {
          this.recoverable.set(r);
          this.recoverLoaded.set(true);
        },
        error: (e) => this.error.set(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  toggleRecoverSel(videoId: string): void {
    const n = new Set(this.recoverSel());
    if (n.has(videoId)) n.delete(videoId);
    else n.add(videoId);
    this.recoverSel.set(n);
  }

  // Selecciona/deselecciona lo VISIBLE (respeta el filtro por lista de origen).
  toggleSelectAllRecover(): void {
    const visible = this.recoverableFiltered().map((r) => r.videoId);
    const sel = new Set(this.recoverSel());
    if (this.allRecoverSelected()) visible.forEach((id) => sel.delete(id));
    else visible.forEach((id) => sel.add(id));
    this.recoverSel.set(sel);
  }

  // Encola la recuperación: crea la lista (si aplica) y deja un pendiente de subida
  // reanudable — la subida real (50u por canción) se hace desde el panel global.
  stageRecovery(): void {
    const sel = this.recoverSel();
    if (sel.size === 0 || this.stagingRecover()) return;
    const targetId = this.recoverTargetId();
    const newTitle = this.recoverNewTitle().trim();
    if (!targetId && !newTitle) {
      this.error.set(this.translate.instant('cross.recover_need_target'));
      return;
    }
    const n = sel.size;
    if (!confirm(this.translate.instant('cross.recover_confirm', { n }))) return;
    this.stagingRecover.set(true);
    this.error.set(null);
    const songs = this.recoverable()
      .filter((r) => sel.has(r.videoId))
      .map((r) => ({ videoId: r.videoId, title: r.title, channelTitle: r.channelTitle, thumbnailUrl: r.thumbnailUrl }));
    this.api.recoverSongs({
      targetPlaylistId: targetId || null,
      newPlaylistTitle: targetId ? null : newTitle,
      songs,
    }).pipe(finalize(() => this.stagingRecover.set(false))).subscribe({
      next: () => {
        this.recoverSel.set(new Set());
        this.recoverNewTitle.set('');
        this.pendingSvc.refresh();   // queda en el chip de pendientes; el usuario sube cuando quiera
      },
      error: (e) => this.error.set(this.apiError.message(e, 'cross.error_scan')),
    });
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
    this.duplicates.set(null);
    this.classification.set(null);
    this.stagedMsg.set(null);
    this.aiError.set(null);
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

  // ── Herramientas de lista: repetidas internas + IA ──
  loadDuplicates(): void {
    if (!this.listId()) return;
    this.loadingDup.set(true);
    this.api.findDuplicates(this.listId())
      .pipe(finalize(() => this.loadingDup.set(false)))
      .subscribe({
        next: (r) => {
          this.duplicates.set(r);
          this.api.refreshQuota();
          this.pickList(this.listId());
        },
        error: (e) => this.error.set(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  cleanDuplicates(): void {
    if (!this.listId()) return;
    if (!confirm(this.translate.instant('detail.confirm_remove'))) return;
    this.cleaning.set(true);
    this.api.removeDuplicates(this.listId(), this.strategy())
      .pipe(finalize(() => this.cleaning.set(false)))
      .subscribe({
        next: (r) => {
          alert(this.translate.instant('detail.alert_removed', { removed: r.removed, kept: r.kept }));
          this.pickList(this.listId());
          this.loadDuplicates();
        },
        error: (e) => this.error.set(this.apiError.message(e, 'cross.error_scan')),
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
    // El 503 (proveedor de IA sin configurar) no lo cubre el helper: es un fallo
    // del clasificador, no de YouTube.
    this.api.classify(this.listId(), this.aiMode())
      .pipe(finalize(() => this.classifying.set(false)))
      .subscribe({
        next: (r) => this.classification.set(r),
        error: (e) => this.aiError.set(
          e?.status === 503
            ? this.translate.instant('detail.ai_config_error')
            : this.translate.instant('detail.ai_generic_error'),
        ),
      });
  }

  // ── Modo por canción: filtros en vivo ──
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

  // ── Borrador local: helpers de estado por tarjeta ──
  // Marca solo cuando el draft difiere de lo que la tarjeta muestra HOY: tras subir
  // desde el panel global el baseline queda obsoleto y no hay nada que señalar.
  hasDraftChanges(videoId: string, currentIds: string[] | undefined): boolean {
    const d = this.drafts()[videoId];
    if (!d) return false;
    const cur = currentIds ?? [];
    return d.desired.length !== cur.length || !d.desired.every((id) => cur.includes(id));
  }

  // Un tag renderizado desde ids actuales está "marcado para quitar" si hay draft
  // y ya no figura en desired.
  isStagedRemoved(videoId: string, playlistId: string): boolean {
    const d = this.drafts()[videoId];
    return !!d && !d.desired.includes(playlistId);
  }

  // Listas agregadas por el modal que aún no existen en el servidor (desired − baseline).
  stagedAdditionRefs(videoId: string): { id: string; title: string }[] {
    const d = this.drafts()[videoId];
    if (!d) return [];
    return this.refsFor(d.desired.filter((id) => !d.baseline.includes(id)));
  }

  // Crea/actualiza el draft; si desired vuelve a igualar baseline, lo elimina.
  private upsertDraft(videoId: string, title: string, baseline: string[], desired: string[]): void {
    const all = { ...this.drafts() };
    const same = baseline.length === desired.length && baseline.every((id) => desired.includes(id));
    if (same) delete all[videoId];
    else all[videoId] = { title, baseline, desired };
    this.drafts.set(all);
  }

  toggleRemoval(videoId: string, title: string, currentIds: string[], playlistId: string): void {
    const existing = this.drafts()[videoId];
    const baseline = existing?.baseline ?? [...currentIds];
    const desired = new Set(existing?.desired ?? baseline);
    if (desired.has(playlistId)) desired.delete(playlistId);
    else desired.add(playlistId);
    this.upsertDraft(videoId, title, baseline, [...desired]);
  }

  discardCard(videoId: string): void {
    const all = { ...this.drafts() };
    delete all[videoId];
    this.drafts.set(all);
  }

  // ── Guardar todo: única acción que manda los drafts a la cola de pendientes ──
  async saveAll(): Promise<void> {
    if (this.draftCount() === 0 || this.savingAll()) return;
    if (!confirm(this.translate.instant('cross.draft_save_confirm', { n: this.draftCount() }))) return;
    // Solo encola: la subida se dispara desde el chip de pendientes cuando el usuario quiera.
    await this.saveAllCore();
  }

  // Cuerpo sin confirm ni apertura de panel: lo reutiliza el guard de salida.
  // Va quitando del record cada draft ya enviado para que un fallo a mitad no
  // duplique al reintentar.
  async saveAllCore(): Promise<boolean> {
    const entries = Object.entries(this.drafts());
    if (!entries.length) return true;
    this.savingAll.set(true);
    this.error.set(null);
    let allOk = true;
    try {
      for (const [videoId, d] of entries) {
        await firstValueFrom(this.api.assignSong({
          videoId,
          title: d.title,
          channelTitle: null,
          thumbnailUrl: null,
          desiredPlaylistIds: d.desired,
        }));
        this.discardCard(videoId);
      }
    } catch (e) {
      this.error.set(this.apiError.message(e, 'cross.assign_error'));
      allOk = false;
    } finally {
      this.savingAll.set(false);
      this.pendingSvc.refresh();
      this.refreshCurrentMode();
      this.loadDupCounts();
    }
    return allOk;
  }

  discardAllDrafts(): void {
    if (!confirm(this.translate.instant('cross.draft_discard_confirm', { n: this.draftCount() }))) return;
    this.drafts.set({});
  }

  // Guard de salida: con drafts pendientes pregunta una vez; OK = guarda y sale.
  async canLeave(): Promise<boolean> {
    if (this.draftCount() === 0) return true;
    const save = confirm(this.translate.instant('cross.draft_leave_confirm', { n: this.draftCount() }));
    if (!save) return false;
    return await this.saveAllCore();
  }

  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(e: BeforeUnloadEvent): void {
    if (this.draftCount() > 0) e.preventDefault();
  }

  // ── Editor de asignación (modal, solo para AGREGAR a listas nuevas) ──
  openEditor(row: SongRow): void {
    this.editingVideoId.set(row.videoId);
    this.editingTitle.set(row.title);
    this.editorLoading.set(true);
    this.api.songLocations(row.videoId)
      .pipe(finalize(() => this.editorLoading.set(false)))
      .subscribe({
        next: (locs) => {
          // Con draft previo la selección arranca del draft (no del servidor) y el
          // baseline original se conserva para no perder las adiciones acumuladas.
          const existing = this.drafts()[row.videoId];
          this.editorBaseline.set(existing?.baseline ?? locs);
          const sel = new Set(existing?.desired ?? locs);
          this.selection.set(sel);
          // Primero las listas donde ya está; el resto alfabético (orden del backend).
          this.editorPlaylists.set(
            [...this.allPlaylists()].sort(
              (a, b) => (sel.has(b.id) ? 1 : 0) - (sel.has(a.id) ? 1 : 0),
            ),
          );
        },
        // Sin ubicaciones el modal abre igual, con todo desmarcado.
        error: () => {
          this.editorBaseline.set([]);
          this.selection.set(new Set());
          this.editorPlaylists.set([...this.allPlaylists()]);
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

  // Aplicar del modal = solo actualiza el borrador local y cierra; sin API.
  apply(): void {
    const vid = this.editingVideoId();
    if (!vid) return;
    this.upsertDraft(vid, this.editingTitle(), this.editorBaseline(), [...this.selection()]);
    this.closeEditor();
  }

  private refreshCurrentMode(): void {
    const m = this.mode();
    if (m === 'repeated' && this.report()) this.scan(false);
    else if (m === 'byList' && this.listId()) this.pickList(this.listId());
    else if (m === 'bySong' && this.results().length) this.search();
    else if (m === 'recover' && this.recoverLoaded()) this.loadRecoverable();
  }

  // Al perder la sesión: sin datos en pantalla (ni de caché) y sin borradores.
  private clearAllData(): void {
    this.report.set(null);
    this.allPlaylists.set([]);
    this.dupCounts.set({});
    this.listId.set('');
    this.listItems.set([]);
    this.locMap.set({});
    this.duplicates.set(null);
    this.classification.set(null);
    this.results.set([]);
    this.nameInput.set('');
    this.idInput.set('');
    this.recoverable.set([]);
    this.recoverLoaded.set(false);
    this.recoverSel.set(new Set());
    this.recoverListFilter.set('');
    this.recoverTargetId.set('');
    this.recoverNewTitle.set('');
    this.drafts.set({});
    this.closeEditor();
    this.error.set(null);
  }

}
