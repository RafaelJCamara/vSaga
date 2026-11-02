import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, inject, signal } from '@angular/core';

/** The one browser storage key of the theme choice: `light` or `dark`; absent means System. */
export const THEME_STORAGE_KEY = 'vsaga.theme';

export type ThemeChoice = 'system' | 'light' | 'dark';

/**
 * Where the theme choice is kept: the browser's `localStorage`, or null where there is none (a browser that blocks
 * site data throws on the very access). With null the choice lives in memory for the page. Per origin, like
 * the guide's state, and nothing about the session is kept here.
 */
export const THEME_STORAGE = new InjectionToken<Storage | null>('THEME_STORAGE', {
  providedIn: 'root',
  factory: () => {
    try {
      return globalThis.localStorage ?? null;
    } catch {
      return null;
    }
  },
});

/**
 * The user's theme: System (follow the OS, `prefers-color-scheme`), Light or Dark. The stylesheet does the
 * rest: a light or dark choice is `data-theme` on `<html>`, System is no attribute (see `src/styles.scss`).
 * `public/theme-init.js` applies a stored choice before the first paint (a file, because the Content Security
 * Policy forbids an inline script), so a choice that differs from the OS does not flash; this service applies it
 * again when the shell is created and on every change. That script repeats `THEME_STORAGE_KEY`; a spec pins the two.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly storage = inject(THEME_STORAGE);
  private readonly root = inject(DOCUMENT).documentElement;

  readonly choice = signal<ThemeChoice>(this.read());

  constructor() {
    this.apply(this.choice());
  }

  set(choice: ThemeChoice): void {
    this.choice.set(choice);
    this.apply(choice);
    try {
      if (choice === 'system') this.storage?.removeItem(THEME_STORAGE_KEY);
      else this.storage?.setItem(THEME_STORAGE_KEY, choice);
    } catch {
      // Storage refused the write (full, or blocked after the read): the choice holds for this page only.
    }
  }

  /** Anything but `light` or `dark` (nothing, garbage, a throwing storage) is System. */
  private read(): ThemeChoice {
    try {
      const stored = this.storage?.getItem(THEME_STORAGE_KEY);
      return stored === 'light' || stored === 'dark' ? stored : 'system';
    } catch {
      return 'system';
    }
  }

  private apply(choice: ThemeChoice): void {
    if (choice === 'system') this.root.removeAttribute('data-theme');
    else this.root.setAttribute('data-theme', choice);
  }
}
