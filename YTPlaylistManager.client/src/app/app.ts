import {
  Component,
  ChangeDetectionStrategy,
  signal,
  computed,
  effect,
  inject,
  OnInit,
} from '@angular/core';
import { RouterOutlet, RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from './services/api.service';
import { AuthService } from './services/auth.service';
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
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);
  protected readonly auth = inject(AuthService);

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

  // Poll de cuota solo con sesión activa: sin login no hay nada que medir y cada
  // tick sería un request de fondo inútil.
  private quotaTimer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    effect(() => {
      const on = this.auth.connected();
      if (on && this.quotaTimer === null) {
        this.api.refreshQuota();
        this.quotaTimer = setInterval(() => this.api.refreshQuota(), 10000);
      } else if (!on && this.quotaTimer !== null) {
        clearInterval(this.quotaTimer);
        this.quotaTimer = null;
      }
    });
  }

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

    this.auth.check();
  }

  triggerPalette(): void {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true }));
  }

  logout(): void {
    this.auth.logout();
  }
}
