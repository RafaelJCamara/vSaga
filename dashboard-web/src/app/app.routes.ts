import { Routes } from '@angular/router';
import { SagaList } from './pages/saga-list/saga-list';
import { SagaDetail } from './pages/saga-detail/saga-detail';
import { anonymousGuard, authGuard, setupGuard } from './guards/auth.guards';

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
  { path: 'sagas', component: SagaList },
  // Both halves of the saga instance identity are in the URL: a correlation id alone can be
  // tracked by more than one saga type.
  { path: 'sagas/:sagaType/:id', component: SagaDetail },
  // A URL that matches nothing (a stale bookmark, a return URL to a page that is gone) goes to the saga list.
  { path: '**', redirectTo: 'sagas' },
];
