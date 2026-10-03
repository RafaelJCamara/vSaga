import { ChangeDetectionStrategy, Component, DestroyRef, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AdminStore } from '../admin.store';

/**
 * The administration area's frame: the heading, the Users / Teams / Roles tabs and, under them, the page of
 * the route. It owns the `AdminStore` (the admin route provides it) and shows the pages only once the store
 * has read everything: a page seeds its draft from the store when it is created, so it must never meet an
 * empty one. A failed read shows its sentence with a way to ask again instead.
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

  protected retry(): void {
    void this.store.load();
  }
}
