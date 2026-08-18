import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { delay, finalize } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { ApiErrorService } from '../../services/api-error.service';
import { PendingService } from '../../services/pending.service';
import { Playlist, RecoverableSong } from '../../models/models';
import { thumbUrl } from '../../utils/youtube.utils';
import { SkeletonList } from '../ui/skeleton-list';
import { EmptyState } from '../ui/empty-state';

/**
 * Canciones huérfanas: conocidas por la app pero fuera de todas las playlists
 * actuales (listas borradas, remociones). Se seleccionan y se encolan hacia una
 * lista existente o nueva; la subida real la hace el panel de pendientes.
 */
@Component({
  selector: 'app-recover-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TranslateModule, SkeletonList, EmptyState],
  templateUrl: './recover-tab.html',
})
export class RecoverTab {
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);
  private readonly translate = inject(TranslateService);
  private readonly pending = inject(PendingService);

  readonly playlists = input<Playlist[]>([]);
  readonly failed = output<string>();

  protected readonly songs = signal<RecoverableSong[]>([]);
  protected readonly loading = signal(false);
  protected readonly loaded = signal(false);
  protected readonly staging = signal(false);

  protected readonly selection = signal<ReadonlySet<string>>(new Set());
  protected readonly targetId = signal<string>('');      // '' = crear lista nueva
  protected readonly newTitle = signal<string>('');
  // Filtro por lista de origen: permite recuperar una lista borrada entera.
  protected readonly listFilter = signal<string>('');

  protected readonly sourceLists = computed(() => {
    const counts = new Map<string, number>();
    for (const r of this.songs()) {
      counts.set(r.lastKnownPlaylist, (counts.get(r.lastKnownPlaylist) ?? 0) + 1);
    }
    return [...counts.entries()]
      .map(([name, n]) => ({ name, n }))
      .sort((a, b) => b.n - a.n);
  });

  protected readonly filtered = computed(() => {
    const f = this.listFilter();
    return f ? this.songs().filter((r) => r.lastKnownPlaylist === f) : this.songs();
  });

  protected readonly allSelected = computed(() => {
    const list = this.filtered();
    if (list.length === 0) return false;
    const sel = this.selection();
    return list.every((r) => sel.has(r.videoId));
  });

  protected thumb(videoId: string): string {
    return thumbUrl(videoId);
  }

  /** La carga es perezosa: solo al entrar por primera vez a la pestaña. */
  ensureLoaded(): void {
    if (!this.loaded()) this.load();
  }

  load(): void {
    this.loading.set(true);
    this.selection.set(new Set());
    this.api.recoverableSongs()
      .pipe(delay(0), finalize(() => this.loading.set(false)))   // delay(0): deja pintar el skeleton
      .subscribe({
        next: (r) => {
          this.songs.set(r);
          this.loaded.set(true);
        },
        error: (e) => this.failed.emit(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  toggle(videoId: string): void {
    const next = new Set(this.selection());
    if (next.has(videoId)) next.delete(videoId);
    else next.add(videoId);
    this.selection.set(next);
  }

  /** Selecciona o deselecciona lo VISIBLE, respetando el filtro de origen. */
  toggleAll(): void {
    const visible = this.filtered().map((r) => r.videoId);
    const sel = new Set(this.selection());
    if (this.allSelected()) visible.forEach((id) => sel.delete(id));
    else visible.forEach((id) => sel.add(id));
    this.selection.set(sel);
  }

  stage(): void {
    const sel = this.selection();
    if (sel.size === 0 || this.staging()) return;
    const target = this.targetId();
    const title = this.newTitle().trim();
    if (!target && !title) {
      this.failed.emit(this.translate.instant('cross.recover_need_target'));
      return;
    }
    if (!confirm(this.translate.instant('cross.recover_confirm', { n: sel.size }))) return;
    this.staging.set(true);
    const songs = this.songs()
      .filter((r) => sel.has(r.videoId))
      .map((r) => ({ videoId: r.videoId, title: r.title, channelTitle: r.channelTitle, thumbnailUrl: r.thumbnailUrl }));
    this.api.recoverSongs({
      targetPlaylistId: target || null,
      newPlaylistTitle: target ? null : title,
      songs,
    })
      .pipe(finalize(() => this.staging.set(false)))
      .subscribe({
        next: () => {
          this.selection.set(new Set());
          this.newTitle.set('');
          this.pending.refresh();   // queda en el chip; el usuario sube cuando quiera
        },
        error: (e) => this.failed.emit(this.apiError.message(e, 'cross.error_scan')),
      });
  }

  /** Al perder la sesión: sin datos en pantalla, ni de caché. */
  clear(): void {
    this.songs.set([]);
    this.loaded.set(false);
    this.selection.set(new Set());
    this.listFilter.set('');
    this.targetId.set('');
    this.newTitle.set('');
  }
}
