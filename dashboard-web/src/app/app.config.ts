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
import { AuthService } from './services/auth.service';
import { reloadOnceOnStaleChunk } from './stale-chunk-reload';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withNavigationErrorHandler(reloadOnceOnStaleChunk)),
    provideHttpClient(withXhr(), withInterceptors([authInterceptor])),
    // The first page is chosen from the session, so the app waits for it (bounded, and never failing: see
    // `AuthService.bootstrap`) before it starts routing.
    provideAppInitializer(() => inject(AuthService).bootstrap()),
  ],
};
