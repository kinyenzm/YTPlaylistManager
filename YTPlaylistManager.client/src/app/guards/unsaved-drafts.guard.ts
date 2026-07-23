import { CanDeactivateFn } from '@angular/router';

// Contrato mínimo: cualquier componente con borradores locales que deba
// confirmar/guardar antes de abandonar la ruta.
export interface HasUnsavedDrafts {
  canLeave(): Promise<boolean> | boolean;
}

export const unsavedDraftsGuard: CanDeactivateFn<HasUnsavedDrafts> = (component) =>
  component.canLeave();
