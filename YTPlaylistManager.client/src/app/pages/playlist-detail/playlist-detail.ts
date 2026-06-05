import {
  Component,
  ChangeDetectionStrategy,
  signal,
  computed,
  effect,
  inject,
  input,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ApiService } from '../../services/api.service';
import { PlaylistItem, DuplicateReport, ClassifyResult } from '../../models/models';

@Component({
  selector: 'app-playlist-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, TranslateModule],
  templateUrl: './playlist-detail.html',
})
export class PlaylistDetail {
  private readonly api = inject(ApiService);
  private readonly translate = inject(TranslateService);

  readonly id = input.required<string>();

  protected readonly items = signal<PlaylistItem[]>([]);
  protected readonly duplicates = signal<DuplicateReport | null>(null);
  protected readonly classification = signal<ClassifyResult | null>(null);
  protected readonly loadingDup = signal(false);
  protected readonly cleaning = signal(false);
  protected readonly classifying = signal(false);
  protected readonly strategy = signal<'videoId' | 'normalizedTitle'>('videoId');
  protected readonly mode = signal<'genre' | 'mood' | 'decade'>('genre');

  protected readonly classKeys = computed(() => {
    const c = this.classification();
    return c ? Object.keys(c.groups) : [];
  });

  constructor() {
    effect(() => {
      const playlistId = this.id();
      if (!playlistId) return;
      this.refreshItems();
      this.duplicates.set(null);
      this.classification.set(null);
    });
  }

  refreshItems(): void {
    this.api.listItems(this.id()).subscribe((r) => this.items.set(r));
  }

  loadDuplicates(): void {
    this.loadingDup.set(true);
    this.api.findDuplicates(this.id()).subscribe({
      next: (r) => {
        this.duplicates.set(r);
        this.loadingDup.set(false);
      },
      error: () => this.loadingDup.set(false),
    });
  }

  cleanDuplicates(): void {
    const msg = this.translate.instant('detail.confirm_remove');
    if (!confirm(msg)) {
      return;
    }
    this.cleaning.set(true);
    this.api.removeDuplicates(this.id(), this.strategy()).subscribe({
      next: (r) => {
        alert(this.translate.instant('detail.alert_removed', { removed: r.removed, kept: r.kept }));
        this.cleaning.set(false);
        this.refreshItems();
        this.loadDuplicates();
      },
      error: () => this.cleaning.set(false),
    });
  }

  classify(): void {
    this.classifying.set(true);
    this.api.classify(this.id(), this.mode()).subscribe({
      next: (r) => {
        this.classification.set(r);
        this.classifying.set(false);
      },
      error: () => this.classifying.set(false),
    });
  }
}
