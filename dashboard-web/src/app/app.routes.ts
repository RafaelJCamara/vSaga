import { Routes } from '@angular/router';
import { SagaList } from './pages/saga-list/saga-list';
import { SagaDetail } from './pages/saga-detail/saga-detail';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'sagas' },
  { path: 'sagas', component: SagaList },
  // Both halves of the saga instance identity are in the URL: a correlation id alone can be
  // tracked by more than one saga type.
  { path: 'sagas/:sagaType/:id', component: SagaDetail },
];
