import { inject, Injectable, signal } from '@angular/core';
import { finalize } from 'rxjs';
import { ApiService } from './api.service';
import { ApiErrorService } from './api-error.service';
import { RefreshAllResult } from '../models/models';

/**
 * "Actualizar todo" vive aquí y no en la página: la operación tarda y el
 * usuario navega. Con el estado en el componente, salir de Inicio perdía el
 * indicador y el resultado aunque el servidor siguiera trabajando. El shell
 * muestra un aviso global mientras corre y la página lee estos signals.
 */
@Injectable({ providedIn: 'root' })
export class RefreshAllService {
  private readonly api = inject(ApiService);
  private readonly apiError = inject(ApiErrorService);

  readonly running = signal(false);
  readonly result = signal<RefreshAllResult | null>(null);
  readonly errorMsg = signal<string | null>(null);

  start(): void {
    if (this.running()) return;
    this.running.set(true);
    this.result.set(null);
    this.errorMsg.set(null);
    this.api.refreshAll()
      .pipe(finalize(() => this.running.set(false)))
      .subscribe({
        next: (r) => {
          this.result.set(r);
          this.api.refreshQuota();
        },
        error: (e) => this.errorMsg.set(this.apiError.message(e, 'playlists.refresh_all_error')),
      });
  }

  dismiss(): void {
    this.result.set(null);
    this.errorMsg.set(null);
  }
}
