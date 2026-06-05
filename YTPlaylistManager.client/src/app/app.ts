import {
  Component,
  ChangeDetectionStrategy,
  signal,
  inject,
  OnInit,
} from '@angular/core';
import { RouterOutlet, RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from './services/api.service';
import { AuthStatus } from './models/models';
import { LangSwitcher } from './components/lang-switcher/lang-switcher';

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
  imports: [RouterOutlet, RouterLink, TranslateModule, LangSwitcher],
  template: `
    <header class="app-header">
      <h2 style="margin:0"><a routerLink="/">{{ 'app.title' | translate }}</a></h2>
      <div class="row">
        <app-lang-switcher />
        @if (status()?.isAuthenticated) {
          <a routerLink="/buscar">{{ 'app.nav.search' | translate }}</a>
          <a routerLink="/repetidas">{{ 'app.nav.cross_dups' | translate }}</a>
          <a routerLink="/cache">{{ 'app.nav.cache' | translate }}</a>
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
  `,
})
export class App implements OnInit {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  protected readonly status = signal<AuthStatus | null>(null);
  protected readonly loginUrl = this.api.loginUrl();

  ngOnInit(): void {
    const lang = detectInitialLang();
    this.translate.use(lang);
    if (typeof document !== 'undefined') {
      document.documentElement.lang = lang;
    }

    this.api.authStatus().subscribe({
      next: (s) => this.status.set(s),
      error: () => this.status.set({ isAuthenticated: false, hasRefreshToken: false }),
    });
  }

  logout(): void {
    this.api.logout().subscribe(() =>
      this.status.set({ isAuthenticated: false, hasRefreshToken: false }),
    );
  }
}
