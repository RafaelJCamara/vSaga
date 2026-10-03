import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors, withXhr } from '@angular/common/http';
import { provideRouter, withNavigationErrorHandler } from '@angular/router';

import { routes } from './app.routes';
import { apiKeyInterceptor } from './interceptors/api-key.interceptor';
import { reloadOnceOnStaleChunk } from './stale-chunk-reload';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withNavigationErrorHandler(reloadOnceOnStaleChunk)),
    provideHttpClient(withXhr(), withInterceptors([apiKeyInterceptor])),
  ]
};
