import { inject, Injectable } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { TimeoutError } from 'rxjs';

/**
 * Traduce un fallo de la API al mensaje que ve el usuario. Una sola escalera
 * para toda la app: timeout → sesión vencida → cuota agotada → el mensaje
 * propio de la pantalla. Sin esto cada pantalla decidía distinto y varias
 * mostraban «¿estás conectado?» con la cuota agotada.
 */
@Injectable({ providedIn: 'root' })
export class ApiErrorService {
  private readonly translate = inject(TranslateService);

  message(e: unknown, fallbackKey: string, fallbackParams?: Record<string, unknown>): string {
    if (e instanceof TimeoutError) return this.translate.instant('common.upload_timeout');
    const status = (e as { status?: number } | null)?.status;
    if (status === 401) return this.translate.instant('common.auth_expired');
    if (status === 403) return this.translate.instant('common.youtube_quota_exhausted');
    return this.translate.instant(fallbackKey, fallbackParams);
  }
}
