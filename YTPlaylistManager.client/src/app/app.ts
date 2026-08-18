import {
  Component,
  ChangeDetectionStrategy,
  computed,
  effect,
  inject,
  OnInit,
} from '@angular/core';
import { Router, RouterOutlet, RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from './services/api.service';
import { AuthService } from './services/auth.service';
import { LangService } from './services/lang.service';
import { RefreshAllService } from './services/refresh-all.service';
import { LangSwitcher } from './components/lang-switcher/lang-switcher';
import { PendingChanges } from './components/pending-changes/pending-changes';
import { CommandPalette } from './components/command-palette/command-palette';

function mapUrlToLang(url: string, es: boolean): string {
  const rules: [RegExp, string][] = es
    ? [[/^\/organize\/list\//, '/organizar/lista/'], [/^\/organize/, '/organizar'], [/^\/data/, '/datos']]
    : [[/^\/organizar\/lista\//, '/organize/list/'], [/^\/organizar/, '/organize'], [/^\/datos/, '/data']];
  for (const [re, replacement] of rules) {
    if (re.test(url)) return url.replace(re, replacement);
  }
  return url;
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
  private readonly router = inject(Router);
  private readonly langSvc = inject(LangService);
  protected readonly auth = inject(AuthService);

  protected readonly loginUrl = this.api.loginUrl();
  // Aviso global de "Actualizar todo": visible en cualquier página mientras corre.
  protected readonly refreshingAll = inject(RefreshAllService).running;
  protected readonly quota = this.api.quota;   // cuota de YouTube restante hoy

  // Idioma actual → rutas en es/en (ambas resuelven; ver app.routes.ts).
  protected readonly lang = this.langSvc.current;
  protected readonly navPaths = computed(() => {
    const es = this.lang().startsWith('es');
    return {
      cross: es ? '/organizar' : '/organize',
      cache: es ? '/datos' : '/data',
    };
  });

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
    // La URL vive en el idioma activo: al cambiarlo, se traduce la ruta actual.
    this.translate.onLangChange.subscribe((e) => {
      const mapped = mapUrlToLang(this.router.url, e.lang.startsWith('es'));
      if (mapped !== this.router.url) this.router.navigateByUrl(mapped);
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
