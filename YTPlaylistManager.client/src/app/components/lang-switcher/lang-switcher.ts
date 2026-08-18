import { Component, ChangeDetectionStrategy, inject } from '@angular/core';
import { TranslateModule } from '@ngx-translate/core';
import { LangService } from '../../services/lang.service';

@Component({
  selector: 'app-lang-switcher',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TranslateModule],
  templateUrl: './lang-switcher.html',
})
export class LangSwitcher {
  private readonly lang = inject(LangService);

  protected readonly current = this.lang.current;

  onChange(lang: string): void {
    this.lang.use(lang);
  }
}
