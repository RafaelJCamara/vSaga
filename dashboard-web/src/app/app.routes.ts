import { Routes } from '@angular/router';
import { SagaList } from './pages/saga-list/saga-list';
import { adminGuard, anonymousGuard, authGuard, setupGuard } from './guards/auth.guards';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'sagas' },
  // The login, setup and account pages are lazy, and their guards run before the chunk is requested: a
  // visitor who is turned away never downloads it.
  {
    path: 'login',
    canActivate: [anonymousGuard],
    loadComponent: () => import('./pages/login/login').then((m) => m.Login),
  },
  {
    path: 'setup',
    canActivate: [setupGuard],
    loadComponent: () => import('./pages/setup/setup').then((m) => m.Setup),
  },
  {
    path: 'account',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/account/account').then((m) => m.Account),
  },
  // The saga pages need a signed-in user: the guard sends anyone else to /login (carrying the page to come
  // back to), or to /setup while no user exists.
  { path: 'sagas', component: SagaList, canActivate: [authGuard] },
  // Both halves of the saga instance identity are in the URL: a correlation id alone can be
  // tracked by more than one saga type.
  // Lazy: the detail page (timeline, map, state inspector) is the heaviest in the app, and the initial
  // bundle has no room for it beside the session code. The guard still runs before the chunk is requested.
  {
    path: 'sagas/:sagaType/:id',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/saga-detail/saga-detail').then((m) => m.SagaDetail),
  },
  // The administration area. `canMatch`, not `canActivate`: the route does not match for anyone who may not
  // manage access, so its chunk is never requested for them (the guard sends them to the saga list, or to
  // sign in first). The area is the biggest lazy tree of the app and has no business in the initial bundle.
  {
    path: 'admin',
    canMatch: [adminGuard],
    loadChildren: () => import('./pages/admin/admin.routes').then((m) => m.ADMIN_ROUTES),
  },
  // A URL that matches nothing (a stale bookmark, a return URL to a page that is gone) goes to the saga list.
  { path: '**', redirectTo: 'sagas' },
];
