import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Tarjeta de "no hay nada" o de error. El texto llega proyectado, ya traducido. */
@Component({
  selector: 'app-empty-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="card empty-card" [class.danger]="danger()"><ng-content /></div>
  `,
})
export class EmptyState {
  readonly danger = input(false);
}
