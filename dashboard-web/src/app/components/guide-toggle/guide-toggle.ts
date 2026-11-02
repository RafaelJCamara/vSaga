import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { USER_GUIDE_URL } from '../../services/guide-areas';
import { GuideService } from '../../services/guide.service';

/**
 * Guide mode in the top bar, for a signed-in user: the "Guide" switch (a toggle button, `aria-pressed`),
 * "Replay tour" while there is a tour to repeat, and a link to the user guide. The one-time offer of guide
 * mode is `app-guide-hint`, a bar of its own under the top bar.
 */
@Component({
  selector: 'app-guide-toggle',
  templateUrl: './guide-toggle.html',
  styleUrl: './guide-toggle.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
  host: { 'data-tour': 'topbar-guide' },
})
export class GuideToggle {
  protected readonly guide = inject(GuideService);
  protected readonly userGuideUrl = USER_GUIDE_URL;
}
