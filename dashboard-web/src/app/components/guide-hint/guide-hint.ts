import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { GuideService } from '../../services/guide.service';

/**
 * The one-time offer of guide mode, until the user has answered it: a bar in the page flow between the top bar
 * and the content, so it takes its own row and covers nothing, whatever the screen width. It does not take the
 * focus; its region is always in the page, so what appears in it is announced.
 */
@Component({
  selector: 'app-guide-hint',
  templateUrl: './guide-hint.html',
  styleUrl: './guide-hint.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GuideHint {
  protected readonly guide = inject(GuideService);
  private readonly document = inject(DOCUMENT);

  /** Answers the hint. Both answers remove the button that was pressed, and with it the keyboard focus, so
   *  the focus goes to the Guide switch first (a tour that starts takes it from there, and returns it there). */
  protected answer(startTour: boolean): void {
    this.document.querySelector<HTMLElement>('app-guide-toggle .guide-switch')?.focus();
    if (startTour) this.guide.setEnabled(true);
    else this.guide.dismissHint();
  }
}
