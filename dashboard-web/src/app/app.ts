import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { GuideOverlay } from './components/guide-overlay/guide-overlay';
import { GuideHint } from './components/guide-hint/guide-hint';
import { GuideToggle } from './components/guide-toggle/guide-toggle';
import { UserMenu } from './components/user-menu/user-menu';
import { AuthService } from './services/auth.service';
import { DensityService } from './services/density.service';
import { DisplayPreferencesService } from './services/display-preferences.service';
import { GuideService } from './services/guide.service';
import { ThemeService } from './services/theme.service';

/** The shell: the top bar around whichever page the router shows. The sign-in and setup pages render under
 *  the same bar, so it shows the navigation, the guide toggle and the user menu only for a signed-in user.
 *  The guide overlay is the last child of the template, loaded (`@defer`) the first time Guide is on. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, GuideHint, GuideOverlay, GuideToggle, UserMenu],
  templateUrl: './app.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './app.scss',
})
export class App {
  protected readonly auth = inject(AuthService);
  // Created with the shell, so it is following the router before the first page is shown.
  protected readonly guide = inject(GuideService);
  // Created with the shell, so a stored theme and density are on `<html>` before any page is shown.
  protected readonly theme = inject(ThemeService);
  protected readonly density = inject(DensityService);
  // Created with the shell too, so a signed-in session's stored choices are applied as soon as it is known.
  protected readonly preferences = inject(DisplayPreferencesService);
  private readonly document = inject(DOCUMENT);

  /** "Skip to main content": moves the focus to the page's `main`. Not a plain `#main` link, which the router
   *  would read as a navigation to the root. */
  protected skipToContent(event: Event): void {
    event.preventDefault();
    this.document.getElementById('main')?.focus();
  }
}
