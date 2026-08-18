import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { finalize } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { DraftsService } from '../../services/drafts.service';
import { Playlist } from '../../models/models';
import { Modal } from '../ui/modal';
import { SkeletonList } from '../ui/skeleton-list';

/**
 * Editor de asignación: elige en qué listas debe quedar una canción. Aplicar
 * solo actualiza el borrador local; nada sale a la API hasta "Guardar cambios".
 */
@Component({
  selector: 'app-assign-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TranslateModule, Modal, SkeletonList],
  templateUrl: './assign-modal.html',
})
export class AssignModal {
  private readonly api = inject(ApiService);
  private readonly drafts = inject(DraftsService);

  readonly videoId = input.required<string>();
  readonly title = input.required<string>();
  readonly playlists = input<Playlist[]>([]);

  readonly closed = output<void>();

  protected readonly loading = signal(false);
  protected readonly selection = signal<ReadonlySet<string>>(new Set());
  // Estado del servidor al abrir: es el baseline del borrador.
  private readonly baseline = signal<string[]>([]);
  protected readonly ordered = signal<Playlist[]>([]);

  protected readonly selectedCount = computed(() => this.selection().size);

  constructor() {
    effect(() => {
      const vid = this.videoId();
      untracked(() => this.load(vid));
    });
  }

  private load(videoId: string): void {
    this.loading.set(true);
    this.api.songLocations(videoId)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (locs) => {
          // Con borrador previo la selección arranca del borrador, y el baseline
          // original se conserva para no perder las adiciones acumuladas.
          const existing = this.drafts.get(videoId);
          this.baseline.set(existing?.baseline ?? locs);
          const sel = new Set(existing?.desired ?? locs);
          this.selection.set(sel);
          // Primero las listas donde ya está; el resto en el orden del backend.
          this.ordered.set(
            [...this.playlists()].sort((a, b) => (sel.has(b.id) ? 1 : 0) - (sel.has(a.id) ? 1 : 0)),
          );
        },
        // Sin ubicaciones el modal abre igual, con todo desmarcado.
        error: () => {
          this.baseline.set([]);
          this.selection.set(new Set());
          this.ordered.set([...this.playlists()]);
        },
      });
  }

  isChecked(playlistId: string): boolean {
    return this.selection().has(playlistId);
  }

  toggle(playlistId: string): void {
    const next = new Set(this.selection());
    if (next.has(playlistId)) next.delete(playlistId);
    else next.add(playlistId);
    this.selection.set(next);
  }

  apply(): void {
    this.drafts.upsert(this.videoId(), this.title(), this.baseline(), [...this.selection()]);
    this.closed.emit();
  }
}
