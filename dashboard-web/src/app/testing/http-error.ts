/**
 * Failed API calls as the specs of the sign-in pages reject them. Test-only, like the rest of this folder.
 */

import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';

/** An `HttpErrorResponse` with a status, a body and, optionally, response headers (`Retry-After`). */
export function httpError(
  status: number,
  body: unknown = null,
  headers: Record<string, string> = {},
): HttpErrorResponse {
  return new HttpErrorResponse({
    status,
    statusText: 'Error',
    url: '/api/auth/test',
    error: body,
    headers: new HttpHeaders(headers),
  });
}

/** What the browser reports when no answer came at all: status 0 and a progress event for a body. */
export function networkError(): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 0,
    statusText: 'Unknown Error',
    url: '/api/auth/test',
    error: new ProgressEvent('error'),
  });
}

/** A problem document as the API writes it: `title`, `detail`, a `code`, and `errors` for a 400. */
export function problem(
  code: string,
  detail: string,
  errors?: Record<string, string[]>,
): Record<string, unknown> {
  return { title: 'Problem', detail, code, ...(errors ? { errors } : {}) };
}
