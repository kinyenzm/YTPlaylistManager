import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/**
 * Diálogo superpuesto: click en el fondo cierra, click adentro no.
 * Ese par se repetía en las seis ventanas de la app.
 */
@Component({
  selector: 'app-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="modal-backdrop" (click)="dismiss.emit()">
      <div class="modal"
           [class.modal--large]="size() === 'large'"
           [class.modal--wide]="size() === 'wide'"
           [class.modal--tall]="size() === 'tall'"
           (click)="$event.stopPropagation()">
        <ng-content />
      </div>
    </div>
  `,
})
export class Modal {
  readonly size = input<'default' | 'large' | 'wide' | 'tall'>('default');
  readonly dismiss = output<void>();
}
