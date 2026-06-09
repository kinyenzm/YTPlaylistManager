import {
  Component,
  ChangeDetectionStrategy,
  signal,
  computed,
  effect,
  inject,
  input,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { PendingService } from '../../services/pending.service';
import { PlaylistItem, DuplicateReport, ClassifyResult } from '../../models/models';

@Component({
  selector: 'app-playlist-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, TranslateModule],
  templateUrl: './playlist-detail.html',
})
export class PlaylistDetail {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);
  private readonly pending = inject(PendingService);

  readonly id = input.required<string>();

  protected readonly items = signal<PlaylistItem[]>([]);
  protected readonly duplicates = signal<DuplicateReport | null>(null);
  protected readonly classification = signal<ClassifyResult | null>(null);
  protected readonly loadingDup = signal(false);
  protected readonly cleaning = signal(false);
  protected readonly classifying = signal(false);
  protected readonly strategy = signal<'videoId' | 'normalizedTitle'>('videoId');
  protected readonly mode = signal<'genre' | 'mood' | 'decade'>('genre');
  protected readonly stagedMsg = signal<string | null>(null);

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

  constructor() {
    effect(() => {
      const playlistId = this.id();
      if (!playlistId) return;
      this.refreshItems();
      this.duplicates.set(null);
      this.classification.set(null);
      this.stagedMsg.set(null);
    });
    // Refrescar items cuando el panel global sube/descarta cambios
    // (un descarte restaura canciones en la caché local).
    effect(() => {
      if (this.pending.mutations() === 0) return;
      untracked(() => this.refreshItems());
    });
  }

  refreshItems(): void {
    this.api.listItems(this.id()).subscribe((r) => this.items.set(r));
  }

  loadDuplicates(): void {
    this.loadingDup.set(true);
    this.api.findDuplicates(this.id()).subscribe({
      next: (r) => {
        this.duplicates.set(r);
        this.loadingDup.set(false);
        this.api.refreshQuota();
        this.refreshItems();
      },
      error: () => this.loadingDup.set(false),
    });
  }

  cleanDuplicates(): void {
    const msg = this.translate.instant('detail.confirm_remove');
    if (!confirm(msg)) {
      return;
    }
    this.cleaning.set(true);
    this.api.removeDuplicates(this.id(), this.strategy()).subscribe({
      next: (r) => {
        alert(this.translate.instant('detail.alert_removed', { removed: r.removed, kept: r.kept }));
        this.cleaning.set(false);
        this.refreshItems();
        this.loadDuplicates();
      },
      error: () => this.cleaning.set(false),
    });
  }

  private stageRemoval(ids: string[], songTitle: string): void {
    if (ids.length === 0) return;
    const msg = this.translate.instant('detail.dup_confirm', { n: ids.length, title: songTitle });
    if (!confirm(msg)) return;
    this.api.removeItemsFromPlaylist(this.id(), ids).subscribe((r) => {
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
      this.refreshItems();
      this.pending.refresh();
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
    this.classifying.set(true);
    this.api.classify(this.id(), this.mode()).subscribe({
      next: (r) => {
        this.classification.set(r);
        this.classifying.set(false);
      },
      error: () => this.classifying.set(false),
    });
  }
}
