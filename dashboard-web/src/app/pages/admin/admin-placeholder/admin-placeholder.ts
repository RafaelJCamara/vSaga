import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

/**
 * The page of an administration route whose screen is not built yet (users and teams, until their commits).
 * The route is real: it is matched, guarded and lazy like the roles pages, and names itself in `data.page`.
 */
@Component({
  selector: 'app-admin-placeholder',
  template: `<p class="empty" role="status">{{ page }} is not available yet.</p>`,
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class AdminPlaceholder {
  protected readonly page: string = inject(ActivatedRoute).snapshot.data['page'] ?? 'This page';
}
