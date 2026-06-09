import { Injectable, inject, signal, computed } from '@angular/core';
import { ApiService } from './api.service';
import { PendingUpload, PendingSongMove } from '../models/models';

/**
 * Estado compartido de TODOS los cambios pendientes de subir a YouTube
 * (uniones de listas + cambios de canciones). Lo consume el panel global
 * `pending-changes`; las páginas llaman refresh() tras encolar y observan
 * `mutations` para refrescar su data local tras subir/descartar.
 */
@Injectable({ providedIn: 'root' })
export class PendingService {
  private readonly api = inject(ApiService);

  readonly uploads = signal<PendingUpload[]>([]);
  readonly moves = signal<PendingSongMove[]>([]);
  readonly open = signal(false);

  // Contador de subidas/descartes aplicados; las páginas lo observan (con guard
  // de valor 0) para recargar listas/items afectados.
  readonly mutations = signal(0);

  readonly count = computed(() => this.uploads().length + this.moves().length);
  readonly totalQuota = computed(
    () =>
      this.uploads().reduce((a, u) => a + u.estimatedQuotaUnits, 0) +
      this.moves().reduce((a, m) => a + m.estimatedQuotaUnits, 0),
  );

  refresh(): void {
    this.api.pendingUploads().subscribe({
      next: (u) => this.uploads.set(u),
      error: () => this.uploads.set([]),
    });
    this.api.pendingSongMoves().subscribe({
      next: (m) => this.moves.set(m),
      error: () => this.moves.set([]),
    });
  }

  bump(): void {
    this.mutations.update((v) => v + 1);
  }
}
