import { Component, ChangeDetectionStrategy, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslateModule, TranslateService } from '@ngx-translate/core';

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
  selector: 'app-lang-switcher',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TranslateModule],
  template: `
    <select
      class="lang-select"
      [ngModel]="current()"
      (ngModelChange)="onChange($event)"
      [attr.aria-label]="'app.lang.label' | translate"
    >
      <option value="es">{{ 'app.lang.es' | translate }}</option>
      <option value="en">{{ 'app.lang.en' | translate }}</option>
    </select>
  `,
  styles: [`
    .lang-select {
      padding: 4px 8px;
      background: var(--panel);
      color: var(--text);
      border: 1px solid var(--border);
      border-radius: 4px;
      font-size: 0.85em;
      cursor: pointer;
    }
  `],
})
export class LangSwitcher implements OnInit {
  private readonly translate = inject(TranslateService);

  protected readonly current = signal<Lang>(detectInitialLang());

  ngOnInit(): void {
    this.translate.use(this.current());
  }

  onChange(lang: string): void {
    if (!(SUPPORTED as readonly string[]).includes(lang)) return;
    this.current.set(lang as Lang);
    this.translate.use(lang);
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(STORAGE_KEY, lang);
    }
  }
}
