import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { DraftsService } from '../../services/drafts.service';
import { thumbUrl } from '../../utils/youtube.utils';

/**
 * Fila de canción del organizador. La comparten los modos repetidas, por lista
 * y por canción, que antes repetían el mismo bloque con diferencias mínimas:
 * los tags editables, las adiciones preparadas y el pie "Deshacer".
 */
@Component({
  selector: 'app-song-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TranslateModule],
  templateUrl: './song-card.html',
})
export class SongCard {
  protected readonly drafts = inject(DraftsService);

  readonly videoId = input.required<string>();
  readonly title = input.required<string>();
  readonly channelTitle = input<string | null>(null);
  readonly thumbnailUrl = input<string | null>(null);
  /** Listas donde está hoy. Base de los tags y del cálculo de cambios. */
  readonly currentIds = input<string[]>([]);
  readonly titles = input<Record<string, string>>({});
  /** Con lista abierta el enlace apunta a la canción dentro de esa lista. */
  readonly listId = input<string>('');
  /** "en N listas" del modo por canción. */
  readonly countBadge = input<number>(0);
  /** La tarjeta entera abre el editor (modo por canción). */
  readonly selectable = input(false);
  readonly showAssign = input(true);

  readonly assign = output<void>();

  // Videos privados o borrados: se muestran, pero no se pueden reasignar.
  protected readonly unavailable = computed(() => {
    const t = this.title()?.trim().toLowerCase();
    return t === 'private video' || t === 'deleted video';
  });

  protected readonly thumb = computed(() => this.thumbnailUrl() || thumbUrl(this.videoId()));

  protected readonly watchUrl = computed(() => {
    const base = `https://www.youtube.com/watch?v=${this.videoId()}`;
    return this.listId() ? `${base}&list=${this.listId()}` : base;
  });

  protected readonly refs = computed(() => this.toRefs(this.currentIds()));
  protected readonly additions = computed(() => this.toRefs(this.drafts.stagedAdditions(this.videoId())));

  protected readonly changed = computed(() => this.drafts.hasChanges(this.videoId(), this.currentIds()));

  private toRefs(ids: string[] | undefined): { id: string; title: string }[] {
    const t = this.titles();
    return (ids ?? []).map((id) => ({ id, title: t[id] ?? id }));
  }

  protected onCardClick(): void {
    if (this.selectable()) this.assign.emit();
  }
}
