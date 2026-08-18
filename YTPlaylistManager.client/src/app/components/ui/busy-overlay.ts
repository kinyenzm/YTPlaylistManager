import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';

/** Cortina de "trabajando" con barra indeterminada. Recibe claves de i18n. */
@Component({
  selector: 'app-busy-overlay',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TranslateModule],
  template: `
    <div class="busy-overlay">
      <div class="busy-card">
        <h4>{{ titleKey() | translate }}</h4>
        <div class="progress indeterminate"><div class="bar"></div></div>
        <p class="muted">{{ descKey() | translate }}</p>
      </div>
    </div>
  `,
})
export class BusyOverlay {
  readonly titleKey = input.required<string>();
  readonly descKey = input.required<string>();
}
