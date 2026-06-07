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
import { LiveActivity } from './components/live-activity/live-activity';

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
  imports: [RouterOutlet, RouterLink, TranslateModule, LangSwitcher, LiveActivity],
  template: `
    <header class="app-header">
      <h2 style="margin:0"><a routerLink="/">{{ 'app.title' | translate }}</a></h2>
      <div class="row">
        @if (quota(); as q) {
          <span class="tag" [title]="'app.nav.quota_title' | translate"
                [style.background]="q.remaining < 500 ? 'var(--accent)' : 'var(--border)'"
                [style.color]="q.remaining < 500 ? '#fff' : 'var(--text)'">
            ⚡ {{ q.remaining }}/{{ q.limit }}
          </span>
        }
        <app-lang-switcher />
        @if (status()?.isAuthenticated) {
          <a [routerLink]="navPaths().search">{{ 'app.nav.search' | translate }}</a>
          <a [routerLink]="navPaths().cross">{{ 'app.nav.cross_dups' | translate }}</a>
          <a [routerLink]="navPaths().cache">{{ 'app.nav.cache' | translate }}</a>
          <span class="muted">{{ 'app.nav.connected' | translate }}</span>
          <button class="secondary" (click)="logout()">{{ 'app.nav.logout' | translate }}</button>
        } @else {
          <a [href]="loginUrl"><button>{{ 'app.nav.login' | translate }}</button></a>
        }
      </div>
    </header>
    <main>
      <router-outlet />
    </main>
    <app-live-activity />
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
      search: es ? '/buscar' : '/search',
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

  logout(): void {
    this.api.logout().subscribe(() =>
      this.status.set({ isAuthenticated: false, hasRefreshToken: false }),
    );
  }
}
