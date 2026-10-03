import { DestroyRef, inject } from '@angular/core';
import { Router } from '@angular/router';

/** Where a page that could not reach its destination ends up: the saga list. */
const HOME_URL = '/sagas';

/**
 * Whether the component that calls it (in an injection context: a field initializer) has been destroyed.
 * A page that awaits a request must check before it touches the view afterwards: registering a render
 * hook on a destroyed view throws, and a page that was left is no longer the one that navigates.
 */
export function trackDestroyed(): () => boolean {
  let destroyed = false;
  inject(DestroyRef).onDestroy(() => {
    destroyed = true;
  });
  return () => destroyed;
}

/**
 * Leaves a page for `url` once a sign-in, a setup or a password change has succeeded. A destination that
 * cannot be reached (a stale return URL that matches no route, a navigation that was cancelled) must not
 * leave a signed-in user on the form: they go to the saga list instead. Not when the page has been left
 * meanwhile (`stillHere` is false), since then someone else navigated and that is not ours to override.
 * Never rejects.
 */
export async function leaveTo(
  router: Router,
  url: string,
  stillHere: () => boolean,
): Promise<void> {
  const arrived = await router.navigateByUrl(url).then(
    (ok) => ok,
    () => false,
  );
  if (arrived || url === HOME_URL || !stillHere()) return;
  await router.navigateByUrl(HOME_URL).catch(() => false);
}
