import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAuthMock } from '../../testing/auth-mock';
import { answerLoad } from '../../testing/admin';
import { AdminShell } from './admin-shell/admin-shell';
import { ADMIN_ROUTES } from './admin.routes';
import { AdminStore } from './admin.store';

describe('ADMIN_ROUTES', () => {
  it('is one shell that provides the store, around every page of the area', () => {
    expect(ADMIN_ROUTES).toHaveLength(1);
    const [shell] = ADMIN_ROUTES;

    expect(shell.path).toBe('');
    expect(shell.component).toBe(AdminShell);
    expect(shell.providers).toEqual([AdminStore]);
  });

  it('has a list, new and :id for users, teams and roles, with new before :id', () => {
    const children = ADMIN_ROUTES[0].children!;

    expect(children.find((c) => c.path === '')?.redirectTo).toBe('users');
    for (const area of ['users', 'teams', 'roles']) {
      const paths = children.find((c) => c.path === area)?.children?.map((c) => c.path);
      expect(paths, area).toEqual(['', 'new', ':id']);
    }
  });

  it('loads every page lazily', () => {
    const pages = ADMIN_ROUTES[0].children!.flatMap((c) => c.children ?? []);

    expect(pages).toHaveLength(9);
    for (const page of pages) {
      expect(page.component).toBeUndefined();
      expect(page.loadComponent).toBeInstanceOf(Function);
    }
  });

  describe('as the router resolves them', () => {
    let http: HttpTestingController;
    let harness: RouterTestingHarness;

    beforeEach(async () => {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([{ path: 'admin', children: ADMIN_ROUTES }]),
          provideAuthMock(),
          provideHttpClient(),
          provideHttpClientTesting(),
        ],
      });
      http = TestBed.inject(HttpTestingController);
      harness = await RouterTestingHarness.create();
    });

    afterEach(() => http.verify());

    async function visit(url: string): Promise<HTMLElement> {
      await harness.navigateByUrl(url);
      answerLoad(http);
      await new Promise((resolve) => setTimeout(resolve));
      harness.detectChanges();
      await harness.fixture.whenStable();
      return harness.routeNativeElement as HTMLElement;
    }

    it("sends the area's own address to the users", async () => {
      await visit('/admin');

      expect(TestBed.inject(Router).url).toBe('/admin/users');
    });

    it.each([
      ['/admin/teams', 'The team list'],
      ['/admin/teams/new', 'The new-team form'],
      ['/admin/teams/abc', 'The team page'],
    ])('shows %s as a placeholder that names the page: %s', async (url, name) => {
      const el = await visit(url);

      expect(el.querySelector('app-admin-placeholder')?.textContent).toContain(
        `${name} is not available yet.`,
      );
    });

    it.each([
      ['/admin/users', 'app-users-list'],
      ['/admin/users/new', 'app-user-edit'],
      ['/admin/users/abc', 'app-user-edit'],
      ['/admin/roles', 'app-roles-list'],
      ['/admin/roles/new', 'app-role-edit'],
      ['/admin/roles/abc', 'app-role-edit'],
    ])('shows the page of the area at %s', async (url, selector) => {
      const el = await visit(url);

      expect(el.querySelector(selector)).not.toBeNull();
    });
  });
});
