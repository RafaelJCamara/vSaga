import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';

let nextId = 0;

/**
 * A destructive action that asks twice: a button, and after a click the question with "yes" and "Cancel".
 * The retry row of the saga detail page as a component, for every delete of the administration area.
 *
 * Keyboard and screen readers, as the retry row does them:
 * - Opening the question moves focus to Cancel, not to the action: a held or repeated Enter must not run
 *   what it has just asked about. The question is the description of both buttons, so a screen reader reads
 *   it with the focused one.
 * - Escape cancels, and so does Cancel; focus returns to the first button, which is what the swap removed.
 * - Confirming emits `confirmed` and closes the question (a second click before the view has caught up
 *   emits nothing). The caller does the work and sets `busy`, which keeps the first button from asking again.
 * - `busy` and `disabled` (a delete that cannot be done: the role is in use) are `aria-disabled`, not
 *   `disabled`: a disabled button drops the focus it holds (the focus has just returned there, or the question
 *   that closes had it), and a button that cannot be focused cannot say why it is unavailable. `describedBy`
 *   names the element that does ("Used by 2 grants"). Either one closes a question that is open, and focus
 *   goes to the first button when the question had it.
 */
@Component({
  selector: 'app-confirm-button',
  templateUrl: './confirm-button.html',
  styleUrl: './confirm-button.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class ConfirmButton {
  private readonly injector = inject(Injector);

  /** The first button: "Delete role". */
  readonly label = input.required<string>();
  /** The question: "Delete the role Support?" */
  readonly prompt = input.required<string>();
  /** The button that does it: "Yes, delete". */
  readonly confirmLabel = input.required<string>();
  /** The work after the confirmation is running: no new question until it is over. */
  readonly busy = input(false);
  /** The action is not available at the moment; the first button says so (`aria-disabled`) and asks nothing. */
  readonly disabled = input(false);
  /** The id of the element that says why the action is unavailable; the first button is described by it. */
  readonly describedBy = input<string | null>(null);
  /** The user confirmed. Emitted once per confirmation. */
  readonly confirmed = output<void>();

  protected readonly confirming = signal(false);
  protected readonly promptId = `confirm-prompt-${nextId++}`;

  private readonly askButton = viewChild<ElementRef<HTMLButtonElement>>('askButton');
  private readonly cancelButton = viewChild<ElementRef<HTMLButtonElement>>('cancelButton');

  constructor() {
    // A question about something that has become unavailable (or that something else is busy with) must not
    // wait to come back when it is available again; its buttons are gone, so focus needs a home.
    effect(() => {
      const unavailable = this.disabled() || this.busy();
      if (unavailable && untracked(() => this.confirming())) {
        this.confirming.set(false);
        this.focusAfterRender(this.askButton, true);
      }
    });
  }

  /** Whether the first button is unavailable: it stays focusable and says so, and a click asks nothing. */
  protected unavailable(): boolean {
    return this.disabled() || this.busy();
  }

  protected ask(): void {
    if (this.unavailable() || this.confirming()) return;
    this.confirming.set(true);
    this.focusAfterRender(this.cancelButton, false);
  }

  protected cancel(): void {
    if (!this.confirming()) return;
    this.confirming.set(false);
    this.focusAfterRender(this.askButton, false);
  }

  protected confirm(): void {
    if (!this.confirming()) return;
    this.confirming.set(false);
    this.confirmed.emit();
    // Only if the focus was lost with the button that held it: the work may have moved it on.
    this.focusAfterRender(this.askButton, true);
  }

  protected escape(event: Event): void {
    // Escape belongs to the question while it is open (nothing behind it should react).
    event.stopPropagation();
    this.cancel();
  }

  private focusAfterRender(
    target: () => ElementRef<HTMLElement> | undefined,
    onlyIfLost: boolean,
  ): void {
    afterNextRender(
      () => {
        const active = document.activeElement;
        if (onlyIfLost && active !== null && active !== document.body) return;
        target()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }
}
