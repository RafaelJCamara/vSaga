import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { GuideService } from '../../../services/guide.service';
import { AdminStore } from '../admin.store';

/**
 * The administration area's frame: the heading, the Users / Teams / Roles tabs and, under them, the page of
 * the route. It owns the `AdminStore` (the admin route provides it) and shows the pages only once the store
 * has read everything: a page seeds its draft from the store when it is created, so it must never meet an
 * empty one. A failed read shows its sentence with a way to ask again instead (or, when the session no longer
 * holds `access.manage` and asking again cannot help, with the way back to the saga list). A failed refresh
 * after a change keeps the pages and warns, and its button reads the lists again without leaving the page.
 *
 * When a navigation inside the area ends and the focus has been lost (the link, the button or the form that held
 * it went with the page that was left: a link followed, a create or a delete that returns to the list), it goes to
 * the heading of the page that has arrived (`tabindex="-1"`, set here), as the first thing a keyboard or a screen
 * reader user meets there. Focus that is somewhere (a tab link, a field a page has focused, the link of a "no
 * longer exists" notice) is never taken away.
 *
 * Guide mode: the shell announces the area (`areaShown('admin')`) once its pages show, not when the route is
 * opened, so the tour finds the table it points at; and again after each navigation inside the area, because
 * the guide forgets what it knew of a page when one ends. The tabs carry the tour's anchors.
 */
@Component({
  selector: 'app-admin-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './admin-shell.html',
  styleUrl: './admin-shell.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class AdminShell {
  protected readonly store = inject(AdminStore);
  private readonly host: HTMLElement = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly injector = inject(Injector);
  private readonly guide = inject(GuideService);
  /** The navigations that have ended since the shell was created: what makes the guide hear of the area again. */
  private readonly navigations = signal(0);

  constructor() {
    // The shell is created while the router activates the route of the navigation that opens the area, so that
    // navigation's own NavigationEnd follows and is seen here too (it announces the area, and moves the focus
    // if it was lost); every later one is a navigation inside the area.
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        this.navigations.update((count) => count + 1);
        this.focusHeading();
      });
    // From an effect, never from here: the guide resets what it knows about a page when a navigation ends, which
    // is after this runs. The pages show once the store has read everything (a failed refresh keeps them).
    effect(() => {
      this.navigations();
      if (this.store.loaded()) untracked(() => this.guide.areaShown('admin'));
    });
    // The route's injector outlives this component: leaving the area drops what it held, and entering it again reads afresh.
    inject(DestroyRef).onDestroy(() => this.store.clear());
    void this.store.load();
  }

  /** After the page has rendered: the focus, if it was lost, to the page's heading. */
  private focusHeading(): void {
    afterNextRender(
      () => {
        const active = document.activeElement;
        if (active !== null && active !== document.body) return;
        const page = this.host.querySelector('router-outlet')?.nextElementSibling;
        const heading = page?.querySelector<HTMLElement>('h2');
        if (!heading) return;
        heading.setAttribute('tabindex', '-1');
        heading.focus();
      },
      { injector: this.injector },
    );
  }

  /** Reads everything again: the error state, where there is nothing to show. */
  protected retry(): void {
    void this.store.load();
  }

  /** Reads the lists again and nothing else: the pages, and what is typed in them, stay where they are. */
  protected refresh(): void {
    void this.store.refresh();
  }
}
