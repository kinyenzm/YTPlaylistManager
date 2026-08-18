import { inject, Injectable, signal } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';

export const SUPPORTED_LANGS = ['es', 'en'] as const;
export type Lang = (typeof SUPPORTED_LANGS)[number];

const STORAGE_KEY = 'ytpm.lang';

function isSupported(lang: string): lang is Lang {
  return (SUPPORTED_LANGS as readonly string[]).includes(lang);
}

function detectInitialLang(): Lang {
  if (typeof localStorage !== 'undefined') {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved && isSupported(saved)) return saved;
  }
  if (typeof navigator !== 'undefined' && navigator.language?.toLowerCase().startsWith('en')) {
    return 'en';
  }
  return 'es';
}

/**
 * Idioma activo: detección inicial, persistencia y el atributo lang del
 * documento. Único punto de la app que llama a translate.use() — antes lo
 * hacían el shell y el selector por separado, con la detección duplicada.
 */
@Injectable({ providedIn: 'root' })
export class LangService {
  private readonly translate = inject(TranslateService);

  readonly current = signal<Lang>(detectInitialLang());

  constructor() {
    this.translate.use(this.current());
    this.syncDocument(this.current());
    this.translate.onLangChange.subscribe((e) => {
      if (isSupported(e.lang)) this.current.set(e.lang);
      this.syncDocument(e.lang);
    });
  }

  use(lang: string): void {
    if (!isSupported(lang)) return;
    if (typeof localStorage !== 'undefined') localStorage.setItem(STORAGE_KEY, lang);
    this.translate.use(lang);
  }

  private syncDocument(lang: string): void {
    if (typeof document !== 'undefined') document.documentElement.lang = lang;
  }
}
