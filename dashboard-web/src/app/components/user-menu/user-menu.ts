import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService } from '../../services/auth.service';

/**
 * The signed-in user in the top bar: a button with their name that opens a small menu (who they are, the
 * account page, sign out). A menu button as the WAI-ARIA authoring practices describe it: opening moves
 * focus into the menu, the arrow keys (and Home and End) move between its items, Space activates the
 * account link as Enter does, and Escape closes the menu and returns focus to the button. Choosing an
 * item, a click outside, tabbing out of the component and any navigation close it too. The items are not in
 * the tab order (they take focus from the keyboard handlers), so Tab leaves the menu.
 */
@Component({
  selector: 'app-user-menu',
  imports: [RouterLink],
  templateUrl: './user-menu.html',
  styleUrl: './user-menu.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
  host: {
    '(document:click)': 'closeOnOutsideClick($event)',
    '(document:keydown.escape)': 'closeOnEscape()',
    '(focusout)': 'closeWhenFocusLeaves($event)',
  },
})
export class UserMenu {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');

  protected readonly user = this.auth.user;
  protected readonly open = signal(false);
  protected readonly signingOut = signal(false);
  /** What the button says: the display name, else the username (the API key has no user at all). */
  protected readonly label = computed(
    () => this.user()?.displayName || this.user()?.username || 'Account',
  );

  constructor() {
    // The page behind the menu changed (a link, the back button): the menu has done its job.
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.open.set(false));
  }

  protected toggle(): void {
    if (this.signingOut()) return;
    if (this.open()) this.close(true);
    else this.show();
  }

  /** The arrow down on the menu button opens the menu, as on a native one, or goes into it when it is open. */
  protected showFromKey(event: Event): void {
    event.preventDefault();
    if (this.signingOut()) return;
    if (this.open()) this.items()[0]?.focus();
    else this.show();
  }

  /** Moves between the items with the arrow keys, wrapping at both ends. */
  protected moveFocus(event: Event, step: 1 | -1): void {
    event.preventDefault();
    const items = this.items();
    if (items.length === 0) return;
    const at = items.indexOf(this.document.activeElement as HTMLElement);
    items[(at + step + items.length) % items.length].focus();
  }

  /** Home and End go to the first and the last item. */
  protected moveFocusTo(event: Event, end: 'first' | 'last'): void {
    event.preventDefault();
    const items = this.items();
    (end === 'first' ? items[0] : items[items.length - 1])?.focus();
  }

  /** Space activates a link as Enter does (a button needs no help): left alone it would scroll the page. */
  protected activateWithSpace(event: Event): void {
    if (!(event.target instanceof HTMLAnchorElement)) return;
    event.preventDefault();
    event.target.click();
  }

  /** Shift+Tab from an item goes back to the button, which is where the menu was opened: close it there. */
  protected leaveBackwards(event: Event): void {
    event.preventDefault();
    this.close(true);
  }

  /** Closes the menu for a chosen item; focus returns to the button when it was on the item. */
  protected select(): void {
    this.close(this.host.nativeElement.contains(this.document.activeElement));
  }

  protected async signOut(): Promise<void> {
    if (this.signingOut()) return;
    this.signingOut.set(true);
    this.select();
    try {
      // Never rejects; the menu usually goes away with the session, so this only matters when it does not.
      await this.auth.logout();
    } finally {
      this.signingOut.set(false);
    }
  }

  protected closeOnOutsideClick(event: Event): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node | null))
      this.close(false);
  }

  protected closeOnEscape(): void {
    if (this.open()) this.close(true);
  }

  /** Tabbing out closes the menu. A focus that goes nowhere (`relatedTarget` null: the window lost it, or
   *  a click on something that cannot take focus) leaves it to the outside-click handler. */
  protected closeWhenFocusLeaves(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    if (this.open() && next !== null && !this.host.nativeElement.contains(next)) this.close(false);
  }

  private show(): void {
    this.open.set(true);
    afterNextRender(() => this.items()[0]?.focus(), { injector: this.injector });
  }

  private close(returnFocus: boolean): void {
    this.open.set(false);
    if (returnFocus) this.trigger().nativeElement.focus();
  }

  private items(): HTMLElement[] {
    return Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[role="menuitem"]'));
  }
}
