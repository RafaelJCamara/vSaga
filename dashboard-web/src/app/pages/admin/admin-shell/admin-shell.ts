import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AdminStore } from '../admin.store';

/**
 * The administration area's frame: the heading, the Users / Teams / Roles tabs and, under them, the page of
 * the route. It owns the `AdminStore` (the admin route provides it) and shows the pages only once the store
 * has read everything: a page seeds its draft from the store when it is created, so it must never meet an
 * empty one. A failed read shows its sentence with a way to ask again instead (or, when the session no longer
 * holds `access.manage` and asking again cannot help, with the way back to the saga list). A failed refresh
 * after a change keeps the pages and warns, and its button reads the lists again without leaving the page.
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

  constructor() {
    // The route's injector outlives this component: leaving the area drops what it held, and entering it again reads afresh.
    inject(DestroyRef).onDestroy(() => this.store.clear());
    void this.store.load();
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
