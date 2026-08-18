import { computed, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from './api.service';
import { ApiErrorService } from './api-error.service';
import { PendingService } from './pending.service';

/**
 * Borrador local de una canción: baseline = listas donde estaba según la caché
 * al momento de editar; desired = listas donde debe quedar. Nada toca la API
 * hasta "Guardar cambios".
 */
export interface SongDraft {
  title: string;
  baseline: string[];
  desired: string[];
}

/**
 * Cambios acumulados sin guardar, por canción. Vive fuera del componente porque
 * lo consultan también el guard de salida y la barra global.
 */
@Injectable()
export class DraftsService {
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);
  private readonly pending = inject(PendingService);

  private readonly drafts = signal<Record<string, SongDraft>>({});

  readonly count = computed(() => Object.keys(this.drafts()).length);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  /**
   * Marca solo cuando el borrador difiere de lo que la tarjeta muestra HOY:
   * tras subir desde el panel global el baseline queda obsoleto y ya no hay
   * nada que señalar.
   */
  hasChanges(videoId: string, currentIds: string[] | undefined): boolean {
    const d = this.drafts()[videoId];
    if (!d) return false;
    const cur = currentIds ?? [];
    return d.desired.length !== cur.length || !d.desired.every((id) => cur.includes(id));
  }

  /** Un tag pintado desde los ids actuales está "marcado para quitar". */
  isStagedRemoved(videoId: string, playlistId: string): boolean {
    const d = this.drafts()[videoId];
    return !!d && !d.desired.includes(playlistId);
  }

  /** Listas agregadas desde el modal que aún no existen en el servidor. */
  stagedAdditions(videoId: string): string[] {
    const d = this.drafts()[videoId];
    return d ? d.desired.filter((id) => !d.baseline.includes(id)) : [];
  }

  get(videoId: string): SongDraft | undefined {
    return this.drafts()[videoId];
  }

  toggleRemoval(videoId: string, title: string, currentIds: string[], playlistId: string): void {
    const existing = this.drafts()[videoId];
    const baseline = existing?.baseline ?? [...currentIds];
    const desired = new Set(existing?.desired ?? baseline);
    if (desired.has(playlistId)) desired.delete(playlistId);
    else desired.add(playlistId);
    this.upsert(videoId, title, baseline, [...desired]);
  }

  /** Crea o actualiza el borrador; si desired vuelve a igualar baseline, lo borra. */
  upsert(videoId: string, title: string, baseline: string[], desired: string[]): void {
    const all = { ...this.drafts() };
    const same = baseline.length === desired.length && baseline.every((id) => desired.includes(id));
    if (same) delete all[videoId];
    else all[videoId] = { title, baseline, desired };
    this.drafts.set(all);
  }

  discard(videoId: string): void {
    const all = { ...this.drafts() };
    delete all[videoId];
    this.drafts.set(all);
  }

  discardAll(): void {
    this.drafts.set({});
  }

  /**
   * Manda los borradores a la cola de pendientes. Va quitando cada uno ya
   * enviado para que un fallo a mitad no duplique al reintentar.
   */
  async saveAll(): Promise<boolean> {
    const entries = Object.entries(this.drafts());
    if (!entries.length) return true;
    this.saving.set(true);
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
        this.discard(videoId);
      }
    } catch (e) {
      this.error.set(this.apiError.message(e, 'cross.assign_error'));
      allOk = false;
    } finally {
      this.saving.set(false);
      this.pending.refresh();
    }
    return allOk;
  }
}
