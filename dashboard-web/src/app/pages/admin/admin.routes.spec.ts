import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideAuthMock } from '../../testing/auth-mock';
import { adminUser, answerLoad, answerReload } from '../../testing/admin';
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
      ['/admin/users', 'app-users-list'],
      ['/admin/users/new', 'app-user-edit'],
      ['/admin/users/abc', 'app-user-edit'],
      ['/admin/teams', 'app-teams-list'],
      ['/admin/teams/new', 'app-team-edit'],
      ['/admin/teams/abc', 'app-team-edit'],
      ['/admin/roles', 'app-roles-list'],
      ['/admin/roles/new', 'app-role-edit'],
      ['/admin/roles/abc', 'app-role-edit'],
    ])('shows the page of the area at %s', async (url, selector) => {
      const el = await visit(url);

      expect(el.querySelector(selector)).not.toBeNull();
    });

    // The real pages inside the real shell. jsdom's click() does not move the focus: the specs put it where a
    // user's would be, and look at what has it after the navigation.
    describe('the keyboard focus after a navigation', () => {
      const focused = () => document.activeElement;
      const settle = async () => {
        await new Promise((resolve) => setTimeout(resolve));
        harness.detectChanges();
        await harness.fixture.whenStable();
        await new Promise((resolve) => setTimeout(resolve));
        harness.detectChanges();
      };
      const heading = (el: HTMLElement) =>
        el.querySelector<HTMLElement>('router-outlet + * h2') ?? undefined;

      afterEach(() => (document.activeElement as HTMLElement | null)?.blur());

      it('is on the heading of the list after a user is created', async () => {
        const el = await visit('/admin/users/new');
        const type = (id: string, value: string) => {
          const input = el.querySelector<HTMLInputElement>(`#${id}`)!;
          input.value = value;
          input.dispatchEvent(new Event('input'));
        };
        type('user-username', 'dave');
        type('user-display-name', 'Dave');
        type('user-password', 'a long enough password');
        type('user-confirmation', 'a long enough password');
        const create = el.querySelector<HTMLButtonElement>('form.card button[type="submit"]')!;
        create.focus();
        await settle();

        create.click();
        await settle();
        http.expectOne('/api/admin/users').flush(adminUser({ id: 'u-dave', username: 'dave' }));
        await settle();
        answerReload(http);
        await settle();

        expect(TestBed.inject(Router).url).toBe('/admin/users');
        expect(heading(el)?.textContent).toBe('Users');
        expect(focused()).toBe(heading(el));
      });

      it('is on the heading of the user page after Enter on a link of the list', async () => {
        const el = await visit('/admin/users');
        const link = el.querySelector<HTMLAnchorElement>('a[href="/admin/users/u-alice"]')!;
        link.focus();
        expect(focused()).toBe(link);

        link.click();
        await settle();

        expect(TestBed.inject(Router).url).toBe('/admin/users/u-alice');
        expect(heading(el)?.textContent).toBe('alice');
        expect(focused()).toBe(heading(el));
      });

      it('is on the heading of the list after a user is deleted', async () => {
        const el = await visit('/admin/users/u-alice');
        const ask = Array.from(el.querySelectorAll<HTMLButtonElement>('button')).find(
          (b) => b.textContent?.trim() === 'Delete user',
        )!;
        ask.click();
        await settle();
        Array.from(el.querySelectorAll<HTMLButtonElement>('button'))
          .find((b) => b.textContent?.trim() === 'Yes, delete')!
          .click();
        await settle();
        http
          .expectOne('/api/admin/users/u-alice')
          .flush(null, { status: 204, statusText: 'No Content' });
        await settle();
        answerReload(http);
        await settle();

        expect(TestBed.inject(Router).url).toBe('/admin/users');
        expect(focused()).toBe(heading(el));
        expect(heading(el)?.textContent).toBe('Users');
      });

      it('is on the heading of the role list after a role is deleted', async () => {
        const el = await visit('/admin/roles/e4a7c1b9-2d3f-4e5a-8b6c-7d8e9f0a1b2c');
        Array.from(el.querySelectorAll<HTMLButtonElement>('button'))
          .find((b) => b.textContent?.trim() === 'Delete role')!
          .click();
        await settle();
        Array.from(el.querySelectorAll<HTMLButtonElement>('button'))
          .find((b) => b.textContent?.trim() === 'Yes, delete')!
          .click();
        await settle();
        http
          .expectOne('/api/admin/roles/e4a7c1b9-2d3f-4e5a-8b6c-7d8e9f0a1b2c')
          .flush(null, { status: 204, statusText: 'No Content' });
        await settle();
        answerReload(http);
        await settle();

        expect(TestBed.inject(Router).url).toBe('/admin/roles');
        expect(focused()).toBe(heading(el));
        expect(heading(el)?.textContent).toBe('Roles');
      });
    });
  });
});
