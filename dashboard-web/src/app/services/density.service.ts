import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { THEME_STORAGE } from './theme.service';

/** The one browser storage key of the density choice: `compact`; absent means Comfortable. */
export const DENSITY_STORAGE_KEY = 'vsaga.density';

export type DensityChoice = 'comfortable' | 'compact';

/**
 * The user's density: Comfortable (the default spacing) or Compact (the spacing tokens redefined smaller, see
 * `src/styles.scss`). Compact is `data-density="compact"` on `<html>`; Comfortable is no attribute. It is kept
 * where the theme is kept (`THEME_STORAGE`: `localStorage`, or nothing where a browser blocks it, then for the
 * page only), and `public/theme-init.js` applies a stored Compact before the first paint. That script repeats
 * `DENSITY_STORAGE_KEY`; a spec pins the two.
 */
@Injectable({ providedIn: 'root' })
export class DensityService {
  private readonly storage = inject(THEME_STORAGE);
  private readonly root = inject(DOCUMENT).documentElement;

  readonly choice = signal<DensityChoice>(this.read());

  constructor() {
    this.apply(this.choice());
  }

  set(choice: DensityChoice): void {
    this.choice.set(choice);
    this.apply(choice);
    try {
      if (choice === 'comfortable') this.storage?.removeItem(DENSITY_STORAGE_KEY);
      else this.storage?.setItem(DENSITY_STORAGE_KEY, choice);
    } catch {
      // Storage refused the write (full, or blocked after the read): the choice holds for this page only.
    }
  }

  /** Anything but `compact` (nothing, garbage, a throwing storage) is Comfortable. */
  private read(): DensityChoice {
    try {
      return this.storage?.getItem(DENSITY_STORAGE_KEY) === 'compact' ? 'compact' : 'comfortable';
    } catch {
      return 'comfortable';
    }
  }

  private apply(choice: DensityChoice): void {
    if (choice === 'compact') this.root.setAttribute('data-density', 'compact');
    else this.root.removeAttribute('data-density');
  }
}
