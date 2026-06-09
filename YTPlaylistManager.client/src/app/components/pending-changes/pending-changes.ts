import { Component, ChangeDetectionStrategy, signal, inject, OnDestroy } from '@angular/core';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../services/api.service';
import { PendingService } from '../../services/pending.service';
import { PendingSongMove, PendingUpload } from '../../models/models';

/**
 * Panel global de cambios pendientes (estilo "Actividad en YouTube"): chip
 * flotante + modal superpuesto disponible en todas las pestañas. Centraliza
 * subir/descartar (individual y en bloque) de uniones y cambios de canciones,
 * con su costo en unidades. Reemplaza los paneles que vivían en Mis listas,
 * Organizar y el detalle de playlist.
 */
@Component({
  selector: 'app-pending-changes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TranslateModule],
  template: `
    @if (svc.count() > 0 && !svc.open()) {
      <button
        style="position:fixed;left:16px;bottom:16px;z-index:1100;box-shadow:var(--shadow-lg)"
        (click)="svc.open.set(true)">
        <i class="fa-solid fa-cloud-arrow-up"></i>
        {{ 'pending.chip' | translate:{ n: svc.count() } }} · ~{{ svc.totalQuota() }}u
      </button>
    }

    @if (svc.open()) {
      <div class="modal-backdrop" (click)="close()">
        <div class="modal" (click)="$event.stopPropagation()" style="max-width:700px;width:94vw;max-height:86vh;overflow:auto">
          <div class="row" style="justify-content:space-between;align-items:center">
            <h3 style="margin:0"><i class="fa-solid fa-cloud-arrow-up"></i> {{ 'pending.title' | translate }}</h3>
            <button class="secondary" style="padding:2px 10px" (click)="close()"><i class="fa-solid fa-xmark"></i></button>
          </div>
          <p class="muted" style="margin:6px 0 0">{{ 'pending.intro' | translate }}</p>

          @if (msg(); as m) {
            <div class="card" style="border-color:var(--accent2);margin-top:10px">{{ m }}</div>
          }
          @if (error(); as e) {
            <div class="card danger" style="margin-top:10px">{{ e }}</div>
          }

          @if (svc.count() === 0) {
            <div class="card" style="margin-top:10px">{{ 'playlists.pending_empty' | translate }}</div>
          } @else {
            <div class="row" style="justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;margin-top:10px">
              <span class="muted">{{ 'pending.total' | translate:{ q: svc.totalQuota() } }}</span>
              <div class="row" style="gap:6px">
                <button (click)="uploadAll()" [disabled]="busyId() !== null">
                  {{ (busyId() === 'ALL' ? 'playlists.uploading' : 'pending.upload_all') | translate:{ q: svc.totalQuota() } }}
                </button>
                @if (svc.count() > 1) {
                  <button class="secondary" (click)="discardAll()" [disabled]="busyId() !== null">
                    {{ 'pending.discard_all' | translate }}
                  </button>
                }
              </div>
            </div>
          }

          @if (svc.uploads().length > 0) {
            <h4 style="margin:14px 0 4px">{{ 'pending.merges' | translate:{ n: svc.uploads().length } }}</h4>
            @for (pu of svc.uploads(); track pu.id) {
              <div class="card">
                <div class="row" style="justify-content:space-between">
                  <strong>{{ pu.targetPlaylistTitle }}</strong>
                  <span class="tag">{{ 'playlists.pending_count' | translate:{ n: pu.itemCount } }}</span>
                </div>
                <div class="muted" style="margin:4px 0">{{ 'playlists.pending_quota' | translate:{ quota: pu.estimatedQuotaUnits } }}</div>
                @if (pu.sourceTitles.length > 0) {
                  <div class="muted danger" style="margin:4px 0">
                    <i class="fa-solid fa-trash"></i> {{ 'playlists.pending_will_delete' | translate:{ sources: pu.sourceTitles.join(', ') } }}
                  </div>
                }
                <details>
                  <summary class="muted" style="cursor:pointer">{{ 'playlists.pending_show_songs' | translate }}</summary>
                  <div style="margin-top:6px">
                    @for (s of pu.items; track s.videoId) {
                      <div class="row" style="margin-top:6px;align-items:flex-start">
                        @if (s.thumbnailUrl) {
                          <img [src]="s.thumbnailUrl" class="thumb" alt="" />
                        }
                        <div>{{ s.title }}</div>
                      </div>
                    }
                  </div>
                </details>
                <div class="row" style="gap:8px;margin-top:10px">
                  <button (click)="uploadMerge(pu)" [disabled]="busyId() !== null">
                    {{ (busyId() === pu.id ? 'playlists.uploading' : 'playlists.upload_button') | translate:{ quota: pu.estimatedQuotaUnits } }}
                  </button>
                  <button class="secondary" (click)="discardMerge(pu.id)" [disabled]="busyId() !== null">
                    {{ 'playlists.pending_discard' | translate }}
                  </button>
                </div>
              </div>
            }
          }

          @if (svc.moves().length > 0) {
            <h4 style="margin:14px 0 4px">{{ 'pending.moves' | translate:{ n: svc.moves().length } }}</h4>
            @for (m of svc.moves(); track m.id) {
              <div class="card">
                <div class="row" style="justify-content:space-between;align-items:flex-start;gap:8px">
                  <div style="min-width:0">
                    <div style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">{{ m.title }}</div>
                    @if (m.addTo.length) {
                      <div class="row" style="gap:4px;flex-wrap:wrap;margin-top:2px">
                        <span class="muted"><i class="fa-solid fa-plus"></i> {{ 'cross.move_add' | translate }}</span>
                        @for (t of m.addTo; track t) { <span class="tag">{{ t }}</span> }
                      </div>
                    }
                    @if (m.removeFrom.length) {
                      <div class="row" style="gap:4px;flex-wrap:wrap;margin-top:2px">
                        <span class="muted"><i class="fa-solid fa-minus"></i> {{ 'cross.move_remove' | translate }}</span>
                        @for (t of m.removeFrom; track t) { <span class="tag">{{ t }}</span> }
                      </div>
                    }
                  </div>
                  <div class="row" style="gap:6px;flex-shrink:0">
                    <button (click)="uploadMoveOne(m)" [disabled]="busyId() !== null">
                      {{ (busyId() === m.id ? 'cross.uploading' : 'cross.move_upload') | translate:{ quota: m.estimatedQuotaUnits } }}
                    </button>
                    <button class="secondary" (click)="discardMoveOne(m.id)" [disabled]="busyId() !== null">
                      {{ 'cross.move_discard' | translate }}
                    </button>
                  </div>
                </div>
              </div>
            }
          }

          <div class="row" style="justify-content:flex-end;margin-top:12px">
            <button class="secondary" (click)="close()">{{ 'common.close' | translate }}</button>
          </div>
        </div>
      </div>
    }

    @if (busyId() !== null) {
      <div class="busy-overlay">
        <div class="busy-card">
          <h4>{{ 'playlists.merge_busy_title' | translate }}</h4>
          <div class="progress indeterminate"><div class="bar"></div></div>
          <p class="muted" style="margin:12px 0 0">{{ 'playlists.merge_busy_desc' | translate }}</p>
        </div>
      </div>
    }
  `,
})
export class PendingChanges implements OnDestroy {
  protected readonly svc = inject(PendingService);
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  protected readonly busyId = signal<string | null>(null);
  protected readonly msg = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  private readonly poll = setInterval(() => this.svc.refresh(), 30_000);

  constructor() {
    this.svc.refresh();
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

  private fail(e: { status?: number }): void {
    this.busyId.set(null);
    this.error.set(
      e?.status === 403
        ? this.translate.instant('common.youtube_quota_exhausted')
        : this.translate.instant('playlists.upload_error'),
    );
  }

  uploadMerge(pu: PendingUpload): void {
    const confirmMsg = this.translate.instant('playlists.upload_confirm', {
      songs: pu.itemCount,
      sources: pu.sourceTitles.join(', ') || '—',
    });
    if (!confirm(confirmMsg)) return;
    this.busyId.set(pu.id);
    this.msg.set(null);
    this.error.set(null);
    this.api.uploadPending(pu.id).subscribe({
      next: (r) => {
        let m = this.translate.instant('playlists.upload_done', { uploaded: r.uploaded });
        if (r.deletedSources > 0) m += ' ' + this.translate.instant('playlists.upload_deleted', { n: r.deletedSources });
        if (r.paused) m += ' ' + this.translate.instant('playlists.upload_paused', { remaining: r.remainingPending, sources: r.remainingSources });
        this.msg.set(m);
        this.done();
      },
      error: (e) => this.fail(e),
    });
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
    this.api.uploadSongMove(m.id).subscribe({
      next: (r) => {
        let t = this.translate.instant('cross.move_done', { added: r.added, removed: r.removed });
        if (r.paused) t += ' ' + this.translate.instant('cross.move_paused', { rem: r.remainingOps });
        this.msg.set(t);
        this.done();
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
      for (const pu of this.svc.uploads()) {
        const r = await firstValueFrom(this.api.uploadPending(pu.id));
        this.api.refreshQuota();
        parts.push(this.translate.instant('playlists.upload_done', { uploaded: r.uploaded }));
        if (r.deletedSources > 0) parts.push(this.translate.instant('playlists.upload_deleted', { n: r.deletedSources }));
        if (r.paused) {
          parts.push(this.translate.instant('playlists.upload_paused', { remaining: r.remainingPending, sources: r.remainingSources }));
          paused = true;
          break;
        }
      }
      if (!paused && this.svc.moves().length > 0) {
        const br = await firstValueFrom(this.api.uploadAllSongMoves());
        parts.push(this.translate.instant('cross.move_done', { added: br.added, removed: br.removed }));
        if (br.paused) parts.push(this.translate.instant('cross.move_paused', { rem: br.remainingMoves }));
      }
      this.msg.set(parts.join(' '));
    } catch (e) {
      this.fail(e as { status?: number });
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
      this.fail(e as { status?: number });
    } finally {
      this.done();
    }
  }
}
