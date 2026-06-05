import { Component, ChangeDetectionStrategy, signal, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { CrossDuplicateReport } from '../../models/models';

@Component({
  selector: 'app-cross-duplicates',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, TranslateModule],
  templateUrl: './cross-duplicates.html',
})
export class CrossDuplicates {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly report = signal<CrossDuplicateReport | null>(null);

  scan(refresh = false): void {
    this.loading.set(true);
    this.error.set(null);
    this.report.set(null);
    this.api.crossDuplicates(refresh).subscribe({
      next: (r) => {
        this.report.set(r);
        this.loading.set(false);
      },
      error: (e) => {
        this.error.set(
          e?.status === 403
            ? this.translate.instant('common.youtube_quota_exhausted')
            : this.translate.instant('cross.error_scan'),
        );
        this.loading.set(false);
        console.error(e);
      },
    });
  }
}
