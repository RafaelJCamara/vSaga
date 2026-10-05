import { ChangeDetectionStrategy, Component, ElementRef, inject, viewChild } from '@angular/core';
import { USER_GUIDE_URL } from '../../services/guide-areas';
import { GuideService } from '../../services/guide.service';

/**
 * Guide mode in the top bar, for a signed-in user: the "Guide" switch (a toggle button, `aria-pressed`),
 * "Replay tour" while there is a tour to repeat, a link to the user guide, and, until the user has answered
 * it once, a hint under the switch. The hint does not take the focus and does not hide anything; its region
 * is always in the page, so what appears in it is announced.
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
  private readonly guideSwitch = viewChild.required<ElementRef<HTMLButtonElement>>('guideSwitch');

  /** Answers the hint. Both answers remove the button that was pressed, and with it the keyboard focus, so
   *  the focus goes to the Guide switch first (a tour that starts takes it from there, and returns it there). */
  protected answerHint(startTour: boolean): void {
    this.guideSwitch().nativeElement.focus();
    if (startTour) this.guide.setEnabled(true);
    else this.guide.dismissHint();
  }
}
