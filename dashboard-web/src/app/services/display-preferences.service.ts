import { Injectable, effect, inject, untracked } from '@angular/core';
import { DisplayPreferences } from '../models/auth.model';
import { AuthService } from './auth.service';
import { DensityChoice, DensityService } from './density.service';
import { ThemeChoice, ThemeService } from './theme.service';

const THEMES: readonly string[] = ['system', 'light', 'dark', 'high-contrast'];
const DENSITIES: readonly string[] = ['comfortable', 'compact'];

/**
 * Makes the theme and the density follow the account (ADR 0015, part 4). `ThemeService` and `DensityService` still
 * own what is applied and what the browser caches in `localStorage` (which `public/theme-init.js` reads before the
 * first paint, since the Content Security Policy forbids doing that from the session); this service is the bridge
 * between them and `AuthService.preferences`:
 * - A signed-in user's stored choices are applied whenever the session is read. The account wins, so a choice made
 *   in another browser shows up here at the next read (a new sign-in, or returning to the tab).
 * - An account with no stored choice is given the browser's current one, once there is one that is not the default:
 *   nobody loses the choice they had already made. Nothing is uploaded for System and Comfortable, since there is
 *   nothing to lose.
 * - A change made in the user menu (`setTheme`, `setDensity`) applies at once and is written through. A write that
 *   fails leaves the choice on this browser and is tried again at the next session read, instead of the account's
 *   older value replacing what the user just picked.
 * - Anonymous visitors and the API key have no account: their choices stay on the browser, as before.
 *
 * Created with the shell (`App`), so a signed-in session's choices are applied as soon as it is known.
 */
@Injectable({ providedIn: 'root' })
export class DisplayPreferencesService {
  private readonly auth = inject(AuthService);
  private readonly theme = inject(ThemeService);
  private readonly density = inject(DensityService);

  /** The user the account's choices were last applied for; a different one starts over. */
  private syncedUserId: string | null = null;
  /** A choice made here that the account has not confirmed yet. */
  private unsaved = false;
  /** True while `save` is writing; a second call leaves the work to the loop that is running. */
  private writing = false;

  constructor() {
    effect(() => {
      const userId = this.auth.user()?.id ?? null;
      const stored = this.auth.preferences();
      untracked(() => this.follow(userId, stored));
    });
  }

  setTheme(choice: ThemeChoice): void {
    this.theme.set(choice);
    this.changed();
  }

  setDensity(choice: DensityChoice): void {
    this.density.set(choice);
    this.changed();
  }

  private follow(userId: string | null, stored: DisplayPreferences | null): void {
    if (userId !== this.syncedUserId) {
      this.syncedUserId = userId;
      this.unsaved = false;
    }
    if (userId === null) return;

    if (this.unsaved) {
      void this.save();
    } else if (stored === null) {
      if (this.theme.choice() !== 'system' || this.density.choice() !== 'comfortable') {
        this.unsaved = true;
        void this.save();
      }
    } else {
      this.apply(stored);
    }
  }

  private apply(stored: DisplayPreferences): void {
    if (stored.theme !== null && THEMES.includes(stored.theme) && stored.theme !== this.theme.choice())
      this.theme.set(stored.theme as ThemeChoice);
    if (
      stored.density !== null &&
      DENSITIES.includes(stored.density) &&
      stored.density !== this.density.choice()
    )
      this.density.set(stored.density as DensityChoice);
  }

  private changed(): void {
    if (this.auth.user() === null) return;
    this.unsaved = true;
    void this.save();
  }

  /**
   * Writes both choices as they are now, for as long as there are unsaved ones. One write at a time: a choice made
   * while one is in flight is picked up by the loop when that write ends, so the account ends with the last one
   * whatever order the answers come in. The loop's last check and `writing = false` run in one step, with no
   * await between them, so a choice can never arrive in the gap and be left unsent.
   */
  private async save(): Promise<void> {
    if (this.writing) return;
    this.writing = true;
    try {
      while (this.unsaved && this.auth.user() !== null) {
        const sent: DisplayPreferences = { theme: this.theme.choice(), density: this.density.choice() };
        try {
          await this.auth.savePreferences(sent);
        } catch {
          // Kept on this browser and still unsaved: the next session read tries again.
          return;
        }
        if (sent.theme === this.theme.choice() && sent.density === this.density.choice())
          this.unsaved = false;
      }
    } finally {
      this.writing = false;
    }
  }
}
