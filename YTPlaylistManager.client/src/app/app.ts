import {
  Component,
  ChangeDetectionStrategy,
  signal,
  computed,
  inject,
  OnInit,
} from '@angular/core';
import { RouterOutlet, RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from './services/api.service';
import { AuthStatus } from './models/models';
import { LangSwitcher } from './components/lang-switcher/lang-switcher';
import { PendingChanges } from './components/pending-changes/pending-changes';
import { CommandPalette } from './components/command-palette/command-palette';

const STORAGE_KEY = 'ytpm.lang';
const SUPPORTED = ['es', 'en'] as const;
type Lang = (typeof SUPPORTED)[number];

function detectInitialLang(): Lang {
  if (typeof localStorage !== 'undefined') {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved && (SUPPORTED as readonly string[]).includes(saved)) {
      return saved as Lang;
    }
  }
  if (typeof navigator !== 'undefined' && navigator.language?.toLowerCase().startsWith('en')) {
    return 'en';
  }
  return 'es';
}

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, TranslateModule, LangSwitcher, PendingChanges, CommandPalette],
  template: `
    <header class="app-header">
      <div class="app-header__brand">
        <h2><a routerLink="/">{{ 'app.title' | translate }}</a></h2>
        @if (quota(); as q) {
          <span class="app-header__quota"
                [class.app-header__quota--critical]="q.remaining < 500"
                [title]="'app.nav.quota_title' | translate">
            {{ q.remaining }}/{{ q.limit }}
          </span>
        }
      </div>

      <button class="secondary app-header__search-btn" (click)="triggerPalette()">
        <i class="fa-solid fa-magnifying-glass"></i>
        <span class="app-header__search-hint">Ctrl K</span>
      </button>

      <div class="app-header__nav">
        @if (status()?.isAuthenticated) {
          <a [routerLink]="navPaths().cross">{{ 'app.nav.cross_dups' | translate }}</a>
          <a [routerLink]="navPaths().cache">{{ 'app.nav.cache' | translate }}</a>
          <span class="muted">{{ 'app.nav.connected' | translate }}</span>
          <button class="secondary" (click)="logout()">{{ 'app.nav.logout' | translate }}</button>
        } @else {
          <a [href]="loginUrl"><button>{{ 'app.nav.login' | translate }}</button></a>
        }
        <app-lang-switcher />
      </div>
    </header>
    <main>
      <router-outlet />
    </main>
    <app-pending-changes />
    <app-command-palette />
  `,
})
export class App implements OnInit {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  protected readonly status = signal<AuthStatus | null>(null);
  protected readonly loginUrl = this.api.loginUrl();
  protected readonly quota = this.api.quota;   // cuota de YouTube restante hoy

  // Idioma actual → rutas en es/en (ambas resuelven; ver app.routes.ts).
  protected readonly lang = signal<string>(detectInitialLang());
  protected readonly navPaths = computed(() => {
    const es = this.lang().startsWith('es');
    return {
      cross: es ? '/organizar' : '/organize',
      cache: es ? '/datos' : '/data',
    };
  });

  ngOnInit(): void {
    const lang = detectInitialLang();
    this.translate.use(lang);
    if (typeof document !== 'undefined') {
      document.documentElement.lang = lang;
    }
    this.translate.onLangChange.subscribe((e) => {
      this.lang.set(e.lang);
      if (typeof document !== 'undefined') {
        document.documentElement.lang = e.lang;
      }
    });

    this.api.authStatus().subscribe({
      next: (s) => this.status.set(s),
      error: () => this.status.set({ isAuthenticated: false, hasRefreshToken: false }),
    });

    // Cuota: inicial + refresco periódico (también la refrescan las operaciones con costo).
    this.api.refreshQuota();
    setInterval(() => this.api.refreshQuota(), 10000);
  }

  triggerPalette(): void {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true }));
  }

  logout(): void {
    this.api.logout().subscribe(() =>
      this.status.set({ isAuthenticated: false, hasRefreshToken: false }),
    );
  }
}
