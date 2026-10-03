import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { Account } from './pages/account/account';
import { Login } from './pages/login/login';
import { Setup } from './pages/setup/setup';
import { AuthMock, AuthMockOptions, createAuthMock, provideAuthMock } from './testing/auth-mock';

/** Stands in for the saga pages, which need the API and the hub and are not what these specs are about. */
@Component({
  selector: 'app-saga-stub',
  template: 'sagas',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class SagaStub {}

const ANONYMOUS: AuthMockOptions = { status: 'anonymous', user: null, access: null };

describe('routes', () => {
  let auth: AuthMock;
  let harness: RouterTestingHarness;

  async function visit(url: string, options: AuthMockOptions = {}): Promise<void> {
    auth = createAuthMock(options);
    TestBed.configureTestingModule({
      providers: [
        // The real route table, with the saga pages swapped for a stub.
        provideRouter(
          routes.map((route) => (route.component ? { ...route, component: SagaStub } : route)),
        ),
        provideAuthMock(auth),
      ],
    });
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
  }

  const url = () => TestBed.inject(Router).url;
  const shown = () => harness.routeNativeElement as HTMLElement;

  it('loads the login, setup and account pages lazily', () => {
    for (const path of ['login', 'setup', 'account']) {
      const route = routes.find((r) => r.path === path);

      expect(route?.component, path).toBeUndefined();
      expect(route?.loadComponent, path).toBeInstanceOf(Function);
    }
  });

  describe('/login', () => {
    it('shows the sign-in page to an anonymous visitor', async () => {
      await visit('/login', ANONYMOUS);

      expect(url()).toBe('/login');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Login);
    });

    it('sends everyone to setup while no user exists', async () => {
      await visit('/login', { ...ANONYMOUS, setupRequired: true, setupAvailable: true });

      expect(url()).toBe('/setup');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Setup);
    });

    it('sends a signed-in visitor on to the page the sign-in was for', async () => {
      await visit('/login?returnUrl=%2Fsagas%3Fstatus%3DFailed');

      expect(url()).toBe('/sagas?status=Failed');
    });

    it('sends a signed-in visitor with an unsafe return URL to the saga list', async () => {
      await visit('/login?returnUrl=%2F%2Fevil.example');

      expect(url()).toBe('/sagas');
    });
  });

  describe('/setup', () => {
    it('shows the setup page while no user exists', async () => {
      await visit('/setup', { ...ANONYMOUS, setupRequired: true, setupAvailable: true });

      expect(url()).toBe('/setup');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Setup);
    });

    it('sends everyone else to the login page', async () => {
      await visit('/setup', ANONYMOUS);

      expect(url()).toBe('/login');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Login);
    });
  });

  describe('/account', () => {
    it('shows the account page to a signed-in user', async () => {
      await visit('/account');

      expect(url()).toBe('/account');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(Account);
    });

    it('sends an anonymous visitor to the login page, which returns to it', async () => {
      await visit('/account', ANONYMOUS);

      expect(url()).toBe('/login?returnUrl=%2Faccount');
      expect(shown().textContent).toContain('Sign in');
    });

    it('sends everyone to setup while no user exists', async () => {
      await visit('/account', { ...ANONYMOUS, setupRequired: true });

      expect(url()).toBe('/setup');
    });

    it('shows a user who must change the password the page where they do', async () => {
      await visit('/account', {
        user: { id: 'u1', username: 'alice', displayName: 'Alice', mustChangePassword: true },
      });

      expect(url()).toBe('/account');
      expect(shown().textContent).toContain('choose a new password');
    });
  });
});
