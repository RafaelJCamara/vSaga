import { HttpInterceptorFn } from '@angular/common/http';
import { DASHBOARD_API_KEY } from '../api-config';

export const apiKeyInterceptor: HttpInterceptorFn = (req, next) =>
  next(req.clone({ setHeaders: { 'X-Api-Key': DASHBOARD_API_KEY } }));
