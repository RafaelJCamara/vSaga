import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideHttpClient, withInterceptors, withXhr } from '@angular/common/http';
import { provideRouter, withNavigationErrorHandler } from '@angular/router';

import { routes } from './app.routes';
import { authInterceptor } from './interceptors/auth.interceptor';
import { GuidePermissionCheck } from './models/guide.model';
import { AuthService } from './services/auth.service';
import { GUIDE_PERMISSION_CHECK } from './services/guide.service';
import { reloadOnceOnStaleChunk } from './stale-chunk-reload';

/**
 * What the tours ask the session. Without a saga type, whether it holds the permission for at least one
 * (the saga list is about every type, and a user scoped to one type still has the list); with one, the
 * session's own answer for that type. `access.manage` is never scoped, so it is asked of the session as it is.
 */
function guidePermissionCheck(): GuidePermissionCheck {
  const auth = inject(AuthService);
  return (permission, sagaType) =>
    sagaType === undefined && permission !== 'access.manage'
      ? auth.canAny(permission)
      : auth.can(permission, sagaType);
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withNavigationErrorHandler(reloadOnceOnStaleChunk)),
    provideHttpClient(withXhr(), withInterceptors([authInterceptor])),
    // The first page is chosen from the session, so the app waits for it (bounded, and never failing: see
    // `AuthService.bootstrap`) before it starts routing.
    provideAppInitializer(() => inject(AuthService).bootstrap()),
    // Guide mode asks the session which areas and steps the user may see.
    { provide: GUIDE_PERMISSION_CHECK, useFactory: guidePermissionCheck },
  ],
};
