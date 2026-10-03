/**
 * What a failed HTTP call to the dashboard API said, in one shape. Pure: it reads an error and nothing else.
 *
 * The API answers a failure with one of two bodies: a problem document (`title`, `detail`, `status`, an
 * optional `code`, and for a 400 an `errors` map keyed by request path) or, for the saga endpoints, a bare
 * `{ error }` (the scoped list's 400 adds `maxPage`). `problemOf` reads both, so a caller never touches the
 * raw body.
 */

import { HttpErrorResponse } from '@angular/common/http';

export interface Problem {
  /** The HTTP status; 0 when no response arrived (network failure, timeout) or `err` is not an HTTP error. */
  status: number;
  /**
   * The body's `code` when it carries one. The API's list: `unauthenticated`, `forbidden`,
   * `password_change_required`, `antiforgery`, `invalid_credentials`, `validation`, `setup_unavailable`,
   * `username_taken`, `name_taken`, `role_in_use`, `role_immutable`, `last_administrator`, `rate_limited`
   * and `identity_unavailable`.
   */
  code: string | null;
  /** The body's `error`, else `detail`, else `title` (the first that is a non-blank string), else the caller's fallback. */
  message: string;
  /** The body's `errors` with camelCase keys (`grants[0].sagaTypes`); empty when there are none. */
  fieldErrors: Record<string, string[]>;
  /** The `Retry-After` header in seconds, when the response carried one in that form. */
  retryAfterSeconds: number | null;
}

/** `err` as the API's answer. Never throws, whatever `err` is. */
export function problemOf(err: unknown, fallback: string): Problem {
  const response = err instanceof HttpErrorResponse ? err : null;
  const body = asRecord(response?.error);
  return {
    status: response?.status ?? 0,
    code: text(body?.['code']) ?? null,
    message: text(body?.['error']) ?? text(body?.['detail']) ?? text(body?.['title']) ?? fallback,
    fieldErrors: fieldErrorsOf(body?.['errors']),
    retryAfterSeconds: retryAfterOf(response),
  };
}

/** A JSON object, not an array, null or a primitive (a proxy's HTML error page arrives as a string). */
function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() !== '' ? value : undefined;
}

/**
 * The server keys validation errors by camelCase request path already; lowering the first letter of each
 * path segment keeps a body that slipped through in .NET casing (`Grants[0].SagaTypes`) usable. A value is
 * a list of messages (a lone string counts as one); anything else is dropped. Keys that collapse into one
 * after lowering keep all their messages.
 */
function fieldErrorsOf(value: unknown): Record<string, string[]> {
  const errors = asRecord(value);
  const result: Record<string, string[]> = {};
  if (!errors) return result;

  for (const [key, raw] of Object.entries(errors)) {
    const messages = (Array.isArray(raw) ? raw : [raw]).filter(
      (m): m is string => typeof m === 'string',
    );
    if (messages.length === 0) continue;
    const camel = key.replace(
      /(^|\.)([A-Z])/g,
      (_, dot: string, letter: string) => dot + letter.toLowerCase(),
    );
    result[camel] = [...(result[camel] ?? []), ...messages];
  }
  return result;
}

/** Whole seconds only: the API always sends `Retry-After` as a delay, never as a date. */
function retryAfterOf(response: HttpErrorResponse | null): number | null {
  const header = response?.headers.get('Retry-After')?.trim();
  return header && /^\d+$/.test(header) ? Number(header) : null;
}
