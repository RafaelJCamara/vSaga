import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { UserMenu } from './components/user-menu/user-menu';
import { AuthService } from './services/auth.service';

/** The shell: the top bar around whichever page the router shows. The sign-in and setup pages render under
 *  the same bar, so it shows the navigation and the user menu only for a signed-in user. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, UserMenu],
  templateUrl: './app.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './app.scss',
})
export class App {
  protected readonly auth = inject(AuthService);
}
