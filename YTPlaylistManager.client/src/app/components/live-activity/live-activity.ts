import { Component, ChangeDetectionStrategy, signal, OnDestroy } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { environment } from '../../../environments/environment';

interface Ev {
  type: string; // insert | delete | delete-list
  title: string;
  playlist: string;
  videoId: string;
  at: string;
}

@Component({
  selector: 'app-live-activity',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TranslateModule],
  template: `
    @if (events().length > 0) {
      <div style="position:fixed;right:16px;bottom:16px;z-index:1100;width:320px;max-width:92vw">
        <div class="card" style="margin:0;border-color:var(--accent2)">
          <div class="row" style="justify-content:space-between">
            <strong>📡 {{ 'activity.title' | translate }}</strong>
            <div class="row" style="gap:6px">
              <button class="secondary" style="padding:2px 8px" (click)="toggle()">{{ open() ? '▾' : '▸' }}</button>
              <button class="secondary" style="padding:2px 8px" (click)="clear()">✕</button>
            </div>
          </div>
          @if (open()) {
            <div style="max-height:240px;overflow:auto;margin-top:6px">
              @for (e of events(); track $index) {
                <div class="muted" style="font-size:0.85em;margin:3px 0">
                  @switch (e.type) {
                    @case ('insert') { ➕ {{ 'activity.added' | translate:{ title: e.title, playlist: e.playlist } }} }
                    @case ('delete') { ➖ {{ 'activity.removed' | translate:{ title: e.title, playlist: e.playlist } }} }
                    @case ('delete-list') { 🗑 {{ 'activity.deleted_list' | translate:{ title: e.title } }} }
                  }
                </div>
              }
            </div>
          }
        </div>
      </div>
    }
  `,
})
export class LiveActivity implements OnDestroy {
  private es?: EventSource;
  protected readonly events = signal<Ev[]>([]);
  protected readonly open = signal(true);

  constructor() {
    try {
      this.es = new EventSource(`${environment.apiBaseUrl}/activity/stream`);
      this.es.onmessage = (m) => {
        try {
          const e = JSON.parse(m.data) as Ev;
          this.events.update((list) => [e, ...list].slice(0, 40));
        } catch {
          /* ignora líneas no-JSON (comentarios SSE) */
        }
      };
      // EventSource reintenta solo en error; no hacemos nada especial.
    } catch {
      /* SSE no disponible */
    }
  }

  ngOnDestroy(): void {
    this.es?.close();
  }

  toggle(): void {
    this.open.update((v) => !v);
  }

  clear(): void {
    this.events.set([]);
  }
}
