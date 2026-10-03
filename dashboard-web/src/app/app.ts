import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { GuideOverlay } from './components/guide-overlay/guide-overlay';
import { GuideToggle } from './components/guide-toggle/guide-toggle';
import { UserMenu } from './components/user-menu/user-menu';
import { AuthService } from './services/auth.service';
import { GuideService } from './services/guide.service';

/** The shell: the top bar around whichever page the router shows. The sign-in and setup pages render under
 *  the same bar, so it shows the navigation, the guide toggle and the user menu only for a signed-in user.
 *  The guide overlay is the last child of the template, loaded (`@defer`) the first time Guide is on. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, GuideOverlay, GuideToggle, UserMenu],
  templateUrl: './app.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './app.scss',
})
export class App {
  protected readonly auth = inject(AuthService);
  // Created with the shell, so it is following the router before the first page is shown.
  protected readonly guide = inject(GuideService);
}
