import { Injectable, inject, signal, computed } from '@angular/core';
import { ApiService } from './api.service';
import { PendingService } from './pending.service';
import { AuthStatus } from '../models/models';

/**
 * Estado de sesión Google único para toda la app. Los componentes leen `connected`
 * y NO piden datos sin sesión; el interceptor HTTP llama markDisconnected(true)
 * ante un 401 de la API.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);
  private readonly pending = inject(PendingService);

  readonly status = signal<AuthStatus | null>(null);
  // null = aún no verificado; solo true habilita cargas de datos.
  readonly connected = computed(() => this.status()?.isAuthenticated === true);
  readonly checked = computed(() => this.status() !== null);
  // El respaldo en Drive requiere el scope appdata en el grant vigente.
  readonly driveBackupEnabled = computed(() => this.status()?.driveBackupEnabled === true);
  // Dispara el toast "sesión expirada" (401 a mitad de sesión, no logout manual).
  readonly sessionExpired = signal(false);

  check(): void {
    this.api.authStatus().subscribe({
      next: (s) => {
        this.status.set(s);
        if (s.isAuthenticated) this.sessionExpired.set(false);
      },
      error: () => this.status.set({ isAuthenticated: false, hasRefreshToken: false }),
    });
  }

  // Camino único de desconexión (401 o logout): estado + limpieza global.
  // Cada página con datos propios los limpia observando `connected`.
  markDisconnected(expired: boolean): void {
    this.status.set({ isAuthenticated: false, hasRefreshToken: false });
    if (expired) this.sessionExpired.set(true);
    this.pending.clear();
    this.api.quota.set(null);
  }

  dismissExpired(): void {
    this.sessionExpired.set(false);
  }

  logout(): void {
    this.api.logout().subscribe({
      next: () => this.markDisconnected(false),
      error: () => this.markDisconnected(false),
    });
  }
}
