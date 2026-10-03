import { Routes } from '@angular/router';
import { AdminShell } from './admin-shell/admin-shell';
import { AdminStore } from './admin.store';

const placeholder = () =>
  import('./admin-placeholder/admin-placeholder').then((m) => m.AdminPlaceholder);
const usersList = () => import('./users/users-list/users-list').then((m) => m.UsersList);
const userEdit = () => import('./users/user-edit/user-edit').then((m) => m.UserEdit);
const rolesList = () => import('./roles/roles-list/roles-list').then((m) => m.RolesList);
const roleEdit = () => import('./roles/role-edit/role-edit').then((m) => m.RoleEdit);

/**
 * The administration area (`/admin`, lazy: `app.routes.ts` matches it only for a user who holds
 * `access.manage`, so the chunk is never requested for anyone else). One shell around everything, which
 * provides the store; each of users, teams and roles has a list, `new` and `:id`. The pages are lazy in
 * turn, so a manager downloads only the ones they open. `new` comes before `:id`, which would match it.
 */
export const ADMIN_ROUTES: Routes = [
  {
    path: '',
    component: AdminShell,
    // Route-scoped: the store lives and dies with the area, not with the root.
    providers: [AdminStore],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'users' },
      {
        path: 'users',
        children: [
          { path: '', loadComponent: usersList },
          { path: 'new', loadComponent: userEdit },
          { path: ':id', loadComponent: userEdit },
        ],
      },
      {
        path: 'teams',
        children: [
          { path: '', loadComponent: placeholder, data: { page: 'The team list' } },
          { path: 'new', loadComponent: placeholder, data: { page: 'The new-team form' } },
          { path: ':id', loadComponent: placeholder, data: { page: 'The team page' } },
        ],
      },
      {
        path: 'roles',
        children: [
          { path: '', loadComponent: rolesList },
          { path: 'new', loadComponent: roleEdit },
          { path: ':id', loadComponent: roleEdit },
        ],
      },
    ],
  },
];
