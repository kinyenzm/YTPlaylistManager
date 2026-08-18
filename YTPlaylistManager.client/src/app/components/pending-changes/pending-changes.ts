import { Component, ChangeDetectionStrategy, effect, signal, inject, OnDestroy } from '@angular/core';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { firstValueFrom, timeout, finalize } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { ApiErrorService } from '../../services/api-error.service';
import { AuthService } from '../../services/auth.service';
import { PendingService } from '../../services/pending.service';
import { PendingSongMove, PendingUpload } from '../../models/models';
import { BusyOverlay } from '../ui/busy-overlay';
import { EmptyState } from '../ui/empty-state';
import { Modal } from '../ui/modal';

/**
 * Panel global de cambios pendientes: chip flotante + modal superpuesto
 * disponible en todas las pestañas. Centraliza subir/descartar (individual y en
 * bloque) de uniones y cambios de canciones, con su costo en unidades.
 */
@Component({
  selector: 'app-pending-changes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TranslateModule, BusyOverlay, EmptyState, Modal],
  templateUrl: './pending-changes.html',
})
export class PendingChanges implements OnDestroy {
  protected readonly svc = inject(PendingService);
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);
  private readonly auth = inject(AuthService);
  private readonly translate = inject(TranslateService);

  protected readonly busyId = signal<string | null>(null);
  protected readonly msg = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  // Sin sesión no se piden pendientes (ni al iniciar ni en el poll); la limpieza
  // al desconectar la hace AuthService.markDisconnected → pending.clear().
  private readonly poll = setInterval(() => {
    if (this.auth.connected()) this.svc.refresh();
  }, 30_000);

  constructor() {
    effect(() => {
      if (this.auth.connected()) this.svc.refresh();
    });
  }

  ngOnDestroy(): void {
    clearInterval(this.poll);
  }

  close(): void {
    this.svc.open.set(false);
    this.msg.set(null);
    this.error.set(null);
  }

  private done(): void {
    this.busyId.set(null);
    this.api.refreshQuota();
    this.svc.refresh();
    this.svc.bump();
  }

  private fail(e: unknown): void {
    this.busyId.set(null);
    this.error.set(this.apiError.message(e, 'playlists.upload_error'));
  }

  async uploadMerge(pu: PendingUpload): Promise<void> {
    const confirmMsg = this.translate.instant('playlists.upload_confirm', {
      songs: pu.itemCount,
      sources: pu.sourceTitles.join(', ') || '—',
    });
    if (!confirm(confirmMsg)) return;
    this.busyId.set(pu.id);
    this.msg.set(null);
    this.error.set(null);
    const total = pu.itemCount;
    try {
      let totalUploaded = 0;
      while (true) {
        const r = await firstValueFrom(
          this.api.uploadPending(pu.id, total > 0 ? 1 : undefined)
            .pipe(timeout({ each: 120_000 }))
        );
        totalUploaded += r.uploaded;
        if (r.targetMissing || r.targetLocked) {
          this.error.set(this.translate.instant(
            r.targetLocked ? 'pending.target_locked' : 'pending.target_missing',
            { title: r.targetPlaylistTitle }));
          break;
        }
        if (r.paused || r.remainingPending === 0) {
          let m = this.translate.instant('playlists.upload_done', { uploaded: totalUploaded });
          if (r.deletedSources > 0) m += ' ' + this.translate.instant('playlists.upload_deleted', { n: r.deletedSources });
          if (r.paused) m += ' ' + this.translate.instant('playlists.upload_paused', { remaining: r.remainingPending, sources: r.remainingSources });
          this.msg.set(m);
          break;
        }
      }
    } catch (e) {
      this.fail(e);
    } finally {
      this.done();
    }
  }

  discardMerge(id: string): void {
    if (!confirm(this.translate.instant('playlists.pending_discard_confirm'))) return;
    this.api.discardPending(id).subscribe({
      next: () => this.done(),
      error: (e) => this.fail(e),
    });
  }

  uploadMoveOne(m: PendingSongMove): void {
    if (!confirm(this.translate.instant('cross.assign_upload_confirm'))) return;
    this.busyId.set(m.id);
    this.msg.set(null);
    this.error.set(null);
    this.api.uploadSongMove(m.id)
      .pipe(timeout({ each: 120_000 }), finalize(() => this.done()))
      .subscribe({
        next: (r) => {
          let t = this.translate.instant('cross.move_done', { added: r.added, removed: r.removed });
          if (r.paused) t += ' ' + this.translate.instant('cross.move_paused', { rem: r.remainingOps });
          this.msg.set(t);
        },
        error: (e) => this.fail(e),
      });
  }

  discardMoveOne(id: string): void {
    if (!confirm(this.translate.instant('cross.assign_discard_confirm'))) return;
    this.api.discardSongMove(id).subscribe({
      next: () => this.done(),
      error: (e) => this.fail(e),
    });
  }

  async uploadAll(): Promise<void> {
    const confirmMsg = this.translate.instant('pending.upload_all_confirm', {
      n: this.svc.count(),
      q: this.svc.totalQuota(),
    });
    if (!confirm(confirmMsg)) return;
    this.busyId.set('ALL');
    this.msg.set(null);
    this.error.set(null);
    const parts: string[] = [];
    try {
      let paused = false;
      // Los pendientes cuya lista destino ya no existe se saltan: subirlos falla y solo
      // se pueden descartar (el aviso está en su tarjeta).
      for (const pu of this.svc.uploads().filter((u) => !u.targetMissing && !u.targetLocked)) {
        const total = pu.itemCount;
        let totalUploaded = 0;
        while (true) {
          const r = await firstValueFrom(
            this.api.uploadPending(pu.id, total > 0 ? 1 : undefined)
              .pipe(timeout({ each: 120_000 }))
          );
          totalUploaded += r.uploaded;
          this.api.refreshQuota();
          if (r.targetMissing || r.targetLocked) {
            parts.push(this.translate.instant(
              r.targetLocked ? 'pending.target_locked' : 'pending.target_missing',
              { title: r.targetPlaylistTitle }));
            paused = true;   // corta el recorrido: el resto se sube en otra pasada
            break;
          }
          if (r.paused || r.remainingPending === 0) {
            parts.push(this.translate.instant('playlists.upload_done', { uploaded: totalUploaded }));
            if (r.deletedSources > 0) parts.push(this.translate.instant('playlists.upload_deleted', { n: r.deletedSources }));
            if (r.paused) {
              parts.push(this.translate.instant('playlists.upload_paused', { remaining: r.remainingPending, sources: r.remainingSources }));
              paused = true;
            }
            break;
          }
        }
        if (paused) break;
      }
      if (!paused && this.svc.moves().length > 0) {
        const br = await firstValueFrom(this.api.uploadAllSongMoves().pipe(timeout({ each: 120_000 })));
        parts.push(this.translate.instant('cross.move_done', { added: br.added, removed: br.removed }));
        if (br.paused) parts.push(this.translate.instant('cross.move_paused', { rem: br.remainingMoves }));
      }
      this.msg.set(parts.join(' '));
    } catch (e) {
      this.fail(e);
    } finally {
      this.done();
    }
  }

  async discardAll(): Promise<void> {
    if (!confirm(this.translate.instant('pending.discard_all_confirm', { n: this.svc.count() }))) return;
    this.error.set(null);
    try {
      for (const pu of this.svc.uploads()) {
        await firstValueFrom(this.api.discardPending(pu.id));
      }
      if (this.svc.moves().length > 0) {
        await firstValueFrom(this.api.discardAllSongMoves());
      }
    } catch (e) {
      this.fail(e);
    } finally {
      this.done();
    }
  }
}
