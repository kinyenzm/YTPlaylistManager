import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { finalize } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { ApiErrorService } from '../../services/api-error.service';
import { PendingService } from '../../services/pending.service';
import { ClassifyResult, DuplicateReport } from '../../models/models';

/**
 * Herramientas de una lista: buscar y limpiar repetidas internas, y clasificar
 * con IA. Solo depende del id de la lista; no conoce borradores ni modos.
 */
@Component({
  selector: 'app-list-tools',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TranslateModule],
  templateUrl: './list-tools.html',
})
export class ListTools {
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);
  private readonly translate = inject(TranslateService);
  private readonly pending = inject(PendingService);

  readonly playlistId = input.required<string>();
  /** Los items de la lista cambiaron: el contenedor debe recargarla. */
  readonly changed = output<void>();
  readonly failed = output<string>();

  protected readonly duplicates = signal<DuplicateReport | null>(null);
  protected readonly classification = signal<ClassifyResult | null>(null);
  protected readonly loadingDup = signal(false);
  protected readonly cleaning = signal(false);
  protected readonly classifying = signal(false);
  protected readonly strategy = signal<'videoId' | 'normalizedTitle'>('videoId');
  protected readonly aiMode = signal<'genre' | 'mood' | 'decade'>('genre');
  protected readonly stagedMsg = signal<string | null>(null);
  protected readonly aiError = signal<string | null>(null);

  readonly busy = computed(() => this.loadingDup() || this.cleaning() || this.classifying());
  readonly busyKey = computed(() => {
    if (this.cleaning()) return 'detail.busy_cleaning';
    if (this.classifying()) return 'detail.busy_classifying';
    return 'detail.busy_finding';
  });

  protected readonly classKeys = computed(() => {
    const c = this.classification();
    return c ? Object.keys(c.groups) : [];
  });

  // Similares (mismo título, distinto video) primero; exactas después.
  protected readonly dupGroupsSorted = computed(() => {
    const d = this.duplicates();
    if (!d) return [];
    const rank = (m: string) => (m === 'normalizedTitle' ? 0 : 1);
    return [...d.groups].sort((a, b) => rank(a.matchType) - rank(b.matchType));
  });

  /** Limpia lo mostrado al cambiar de lista. */
  reset(): void {
    this.duplicates.set(null);
    this.classification.set(null);
    this.stagedMsg.set(null);
    this.aiError.set(null);
  }

  loadDuplicates(): void {
    if (!this.playlistId()) return;
    this.loadingDup.set(true);
    this.api.findDuplicates(this.playlistId())
      .pipe(finalize(() => this.loadingDup.set(false)))
      .subscribe({
        next: (r) => {
          this.duplicates.set(r);
          this.api.refreshQuota();
          this.changed.emit();
        },
        error: (e) => this.failed.emit(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  cleanDuplicates(): void {
    if (!this.playlistId()) return;
    if (!confirm(this.translate.instant('detail.confirm_remove'))) return;
    this.cleaning.set(true);
    this.api.removeDuplicates(this.playlistId(), this.strategy())
      .pipe(finalize(() => this.cleaning.set(false)))
      .subscribe({
        next: (r) => {
          alert(this.translate.instant('detail.alert_removed', { removed: r.removed, kept: r.kept }));
          this.changed.emit();
          this.loadDuplicates();
        },
        error: (e) => this.failed.emit(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  classify(): void {
    if (!this.playlistId()) return;
    this.classifying.set(true);
    this.aiError.set(null);
    // El 503 (proveedor de IA sin configurar) es un fallo del clasificador, no de YouTube.
    this.api.classify(this.playlistId(), this.aiMode())
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

  removeCopy(it: { playlistItemId: string; title: string }): void {
    this.stageRemoval([it.playlistItemId], it.title);
  }

  keepThis(items: { playlistItemId: string; title: string }[], keepId: string): void {
    const toRemove = items.filter((i) => i.playlistItemId !== keepId);
    this.stageRemoval(toRemove.map((i) => i.playlistItemId), items[0]?.title ?? '');
  }

  private stageRemoval(ids: string[], songTitle: string): void {
    if (ids.length === 0) return;
    if (!confirm(this.translate.instant('detail.dup_confirm', { n: ids.length, title: songTitle }))) return;
    this.api.removeItemsFromPlaylist(this.playlistId(), ids).subscribe((r) => {
      this.api.refreshQuota();
      this.stagedMsg.set(this.translate.instant('detail.dup_staged', { n: r.staged }));
      // Poda local: NO se re-lee de YouTube, porque la remoción todavía no está
      // allá y releer restauraría la caché "deshaciendo" lo quitado.
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
      this.changed.emit();
      this.pending.refresh();
    });
  }
}
