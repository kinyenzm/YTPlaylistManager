import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';

/**
 * Reacción global a errores de la API:
 * - 401 (fuera de /auth/*): la sesión murió a mitad de uso → navbar a "Conectar",
 *   toast de sesión expirada y limpieza de estado global.
 * - 403 (fuera de /quota): cuota agotada → refrescar el marcador ya (el backend
 *   fijó Used=límite con MarkExhausted; el badge cae a 0 sin esperar el poll).
 */
export const sessionInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const api = inject(ApiService);
  return next(req).pipe(
    catchError((err: unknown) => {
      const status = err instanceof HttpErrorResponse ? err.status : 0;
      if (status === 401 && !req.url.includes('/auth/')) {
        auth.markDisconnected(true);
      }
      if (status === 403 && !req.url.includes('/quota')) {
        api.refreshQuota();
      }
      return throwError(() => err);
    }),
  );
};
