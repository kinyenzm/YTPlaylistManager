import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * Placeholder de carga. Cuatro formas según lo que se esté esperando:
 * canciones, opciones de una lista, tarjetas de playlist o contadores.
 * Antes cada pantalla repetía su propio bloque, tres de ellos idénticos.
 */
type SkeletonVariant = 'song' | 'option' | 'card' | 'stat';

@Component({
  selector: 'app-skeleton-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div [class]="containerClass()">
      @for (_ of rows(); track $index) {
        @switch (variant()) {
          @case ('option') {
            <div class="card--skeleton">
              <div class="row">
                <span class="skeleton skeleton--text skeleton--wide"></span>
                <span class="skeleton skeleton--meta skeleton--badge"></span>
              </div>
            </div>
          }
          @case ('card') {
            <div class="card--skeleton">
              <div class="row"><span class="skeleton skeleton--thumb-lg"></span></div>
              <span class="skeleton skeleton--title"></span>
              <span class="skeleton skeleton--text skeleton--half"></span>
              <span class="skeleton skeleton--meta"></span>
            </div>
          }
          @case ('stat') {
            <div class="stat-card">
              <span class="skeleton skeleton--icon"></span>
              <div class="skeleton__stack">
                <span class="skeleton skeleton--text skeleton--half"></span>
                <span class="skeleton skeleton--title skeleton--short"></span>
              </div>
            </div>
          }
          @default {
            <div class="card--skeleton">
              <div class="row">
                <span class="skeleton skeleton--thumb-lg"></span>
                <div class="flex-fill">
                  <span class="skeleton skeleton--title"></span>
                  <span class="skeleton skeleton--text skeleton--half"></span>
                </div>
              </div>
            </div>
          }
        }
      }
    </div>
  `,
})
export class SkeletonList {
  readonly count = input(5);
  readonly variant = input<SkeletonVariant>('song');

  protected readonly rows = computed(() => Array.from({ length: this.count() }));
  protected readonly containerClass = computed(() => {
    switch (this.variant()) {
      case 'card': return 'grid';
      case 'stat': return 'grid grid--stats';
      default: return 'cross-org__song-list';
    }
  });
}
