import { DOCUMENT } from '@angular/common';
import { inject } from '@angular/core';
import { NavigationError } from '@angular/router';
import { PAGE_RELOAD } from './services/auth.service';

/** `sessionStorage` key of the time of the last reload this handler caused (milliseconds since the epoch). */
export const STALE_CHUNK_RELOAD_KEY = 'vsaga.stale-chunk-reload-at';

/** At most one reload per this long: a chunk that is missing for another reason must not reload forever. */
export const STALE_CHUNK_RELOAD_INTERVAL_MS = 60_000;

/** What the browsers say when a dynamic `import()` fails: Chromium "Failed to fetch dynamically imported
 *  module", Firefox "error loading dynamically imported module", Safari "Importing a module script failed". */
const IMPORT_FAILURE = /dynamically imported module|module script/i;

/** Whether `error` is a lazy chunk that could not be loaded. */
export function isStaleChunkError(error: unknown): boolean {
  const message =
    error instanceof Error ? error.message : typeof error === 'string' ? error : undefined;
  return message !== undefined && IMPORT_FAILURE.test(message);
}

/**
 * The router's navigation error handler (`withNavigationErrorHandler`, so it runs in an injection
 * context). A tab that stayed open while the web image was rebuilt asks for lazy chunks whose hashed
 * names no longer exist; the import fails, and so does every navigation to that page until the tab loads
 * the new `index.html`. Reloads the page once for that, and never more than once a minute: the time of
 * the last reload is kept in `sessionStorage`, and when it cannot be read or written (a browser that
 * blocks storage) nothing is reloaded, because without the record a chunk that stays missing would
 * reload the page in a loop. Nor while the browser is offline: the import failed for want of a network,
 * not because the chunk is gone, and a reload would only replace the page with the browser's error page.
 * Any other navigation error is left to the router.
 */
export function reloadOnceOnStaleChunk(navigationError: NavigationError): void {
  if (!isStaleChunkError(navigationError.error)) return;
  const reload = inject(PAGE_RELOAD);
  const document = inject(DOCUMENT);
  if (document.defaultView?.navigator.onLine === false) return;
  if (!recordReload(document, Date.now())) return;
  reload();
}

/** Whether a reload is due, which it only is when the time of this one could be written down. */
function recordReload(document: Document, now: number): boolean {
  try {
    const storage = document.defaultView?.sessionStorage;
    if (!storage) return false;
    const last = Number(storage.getItem(STALE_CHUNK_RELOAD_KEY));
    // A record from the future (a clock that stepped back) is as stale as an old one.
    if (last > 0 && last <= now && now - last < STALE_CHUNK_RELOAD_INTERVAL_MS) return false;
    storage.setItem(STALE_CHUNK_RELOAD_KEY, String(now));
    return storage.getItem(STALE_CHUNK_RELOAD_KEY) === String(now);
  } catch {
    return false;
  }
}
