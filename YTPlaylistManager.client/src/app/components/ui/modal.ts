import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  afterNextRender,
  inject,
  input,
  output,
} from '@angular/core';

let nextId = 0;

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])';

/**
 * Diálogo superpuesto: click en el fondo cierra, click adentro no.
 * Ese par se repetía en las seis ventanas de la app.
 *
 * Accesibilidad: role="dialog" con su título como nombre, Escape cierra, el foco entra
 * al abrir, Tab no se escapa al fondo y al cerrar vuelve a donde estaba.
 */
@Component({
  selector: 'app-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'onKey($event)' },
  template: `
    <div class="modal-backdrop" (click)="dismiss.emit()">
      <div class="modal"
           role="dialog"
           aria-modal="true"
           tabindex="-1"
           [attr.aria-labelledby]="titleId"
           [class.modal--large]="size() === 'large'"
           [class.modal--wide]="size() === 'wide'"
           [class.modal--tall]="size() === 'tall'"
           (click)="$event.stopPropagation()">
        <ng-content />
      </div>
    </div>
  `,
})
export class Modal implements OnDestroy {
  readonly size = input<'default' | 'large' | 'wide' | 'tall'>('default');
  readonly dismiss = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly returnFocus = document.activeElement as HTMLElement | null;
  protected readonly titleId = `modal-title-${++nextId}`;

  constructor() {
    afterNextRender(() => {
      const dialog = this.dialog();
      if (!dialog) return;
      // El primer encabezado del contenido nombra el diálogo.
      const heading = dialog.querySelector('h1, h2, h3, h4');
      if (heading && !heading.id) heading.id = this.titleId;
      else if (!heading) dialog.removeAttribute('aria-labelledby');
      (this.focusables()[0] ?? dialog).focus();
    });
  }

  ngOnDestroy(): void {
    if (this.returnFocus?.isConnected) this.returnFocus.focus();
  }

  protected onKey(e: KeyboardEvent): void {
    // La paleta de comandos (Ctrl+K) puede abrirse encima de un modal y escucha antes:
    // si ya manejó la tecla, el modal no la repite.
    if (e.defaultPrevented) return;
    if (e.key === 'Escape') {
      e.preventDefault();
      this.dismiss.emit();
      return;
    }
    if (e.key !== 'Tab') return;
    const items = this.focusables();
    if (items.length === 0) {
      e.preventDefault();
      return;
    }
    const first = items[0];
    const last = items[items.length - 1];
    const active = document.activeElement;
    if (e.shiftKey && (active === first || !this.dialog()?.contains(active))) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && (active === last || !this.dialog()?.contains(active))) {
      e.preventDefault();
      first.focus();
    }
  }

  private dialog(): HTMLElement | null {
    return this.host.nativeElement.querySelector('.modal');
  }

  private focusables(): HTMLElement[] {
    const dialog = this.dialog();
    if (!dialog) return [];
    return Array.from(dialog.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
      (el) => el.offsetParent !== null || el === document.activeElement,
    );
  }
}
