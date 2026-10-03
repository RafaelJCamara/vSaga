import { ChangeDetectionStrategy, Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { adminData, answerLoad } from '../../../testing/admin';
import { AdminStore } from '../admin.store';
import { AdminShell } from './admin-shell';

@Component({
  selector: 'app-page-stub',
  template: '<p id="page">a page</p>',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class PageStub {}

/** The admin route tree as `ADMIN_ROUTES` has it, with a stub for every page, mounted where the app mounts it. */
const ROUTES: Routes = [
  {
    path: 'admin',
    children: [
      {
        path: '',
        component: AdminShell,
        providers: [AdminStore],
        children: [
          { path: 'users', component: PageStub },
          { path: 'teams', component: PageStub },
          {
            path: 'roles',
            children: [
              { path: '', component: PageStub },
              { path: 'new', component: PageStub },
            ],
          },
        ],
      },
    ],
  },
  { path: 'elsewhere', component: PageStub },
];

describe('AdminShell', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter(ROUTES), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => http.verify());

  const el = () => harness.routeNativeElement as HTMLElement;
  const tabs = () =>
    Array.from(el().querySelectorAll<HTMLAnchorElement>('nav[aria-label="Administration"] a'));
  const page = () => el().querySelector('#page');
  const store = () => harness.routeDebugElement!.injector.get(AdminStore);
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve));
    harness.detectChanges();
  };

  /** Opens the area and answers its reads, so the pages show. */
  async function open(url = '/admin/users'): Promise<void> {
    await harness.navigateByUrl(url);
    answerLoad(http);
    await settle();
  }

  it('shows the heading and the Users, Teams and Roles tabs, in that order', async () => {
    await harness.navigateByUrl('/admin/users');

    expect(el().querySelector('h1')?.textContent).toBe('Administration');
    expect(tabs().map((a) => [a.textContent?.trim(), a.getAttribute('href')])).toEqual([
      ['Users', '/admin/users'],
      ['Teams', '/admin/teams'],
      ['Roles', '/admin/roles'],
    ]);
    expect(el().querySelector('nav.subtabs')).not.toBeNull();
    answerLoad(http);
  });

  it('reads everything when it opens, and shows no page until that is done', async () => {
    await harness.navigateByUrl('/admin/users');

    expect(page()).toBeNull();
    expect(el().querySelector('[role="status"]')?.textContent).toContain('Loading');
    // The tabs are there while it loads: a manager can see where they are.
    expect(tabs()).toHaveLength(3);
    expect(store().loaded()).toBe(false);

    answerLoad(http);
    await settle();

    expect(store().loaded()).toBe(true);
    expect(page()).not.toBeNull();
    expect(el().querySelector('[role="status"]')).toBeNull();
  });

  it('marks the tab of the page the manager is on, for the list and for a page below it', async () => {
    await open('/admin/roles/new');

    const state = () =>
      tabs().map((a) => [
        a.textContent?.trim(),
        a.classList.contains('active'),
        a.getAttribute('aria-current'),
      ]);
    expect(state()).toEqual([
      ['Users', false, null],
      ['Teams', false, null],
      ['Roles', true, 'page'],
    ]);

    await harness.navigateByUrl('/admin/teams');
    expect(state()).toEqual([
      ['Users', false, null],
      ['Teams', true, 'page'],
      ['Roles', false, null],
    ]);
  });

  it('keeps the store for the pages and reads only once while the manager moves between them', async () => {
    await open('/admin/users');

    await harness.navigateByUrl('/admin/roles');
    await harness.navigateByUrl('/admin/teams');

    http.expectNone((r) => r.url.startsWith('/api/'));
    expect(page()).not.toBeNull();
  });

  describe('when the read fails', () => {
    async function fail(): Promise<void> {
      await harness.navigateByUrl('/admin/users');
      // The first refusal ends the read; the others are cancelled and left unanswered.
      http
        .match(() => true)
        .find((r) => r.request.url === '/api/admin/users')!
        .flush({ title: 'Boom' }, { status: 500, statusText: 'Boom' });
      await settle();
    }

    it('says so in an alert in place of the pages, and offers to try again', async () => {
      await fail();

      const alert = el().querySelector('.banner--error[role="alert"]');
      expect(alert?.textContent).toContain('HTTP 500');
      expect(alert?.querySelector('button')?.textContent?.trim()).toBe('Try again');
      expect(page()).toBeNull();
    });

    it('reads again when asked, and shows the pages when that works', async () => {
      await fail();

      el().querySelector<HTMLButtonElement>('.banner--error button')!.click();
      await settle();
      expect(el().querySelector('.banner--error')).toBeNull();
      expect(el().querySelector('[role="status"]')?.textContent).toContain('Loading');

      answerLoad(http);
      await settle();

      expect(page()).not.toBeNull();
      expect(el().querySelector('[role="alert"]')).toBeNull();
    });
  });

  it('keeps the pages and warns when a later read of the lists fails, with the way to read again', async () => {
    await open('/admin/users');
    const saving = store().deleteTeam('t');
    http.expectOne('/api/admin/teams/t').flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    http.expectOne('/api/admin/teams').flush([]);
    http.expectOne('/api/admin/roles').flush([]);
    http.expectOne('/api/admin/users').error(new ProgressEvent('error'));
    await saving;
    await settle();

    expect(page()).not.toBeNull();
    const warning = el().querySelector('.banner--warning[role="alert"]');
    expect(warning?.textContent).toContain('Cannot reach');
    expect(warning?.querySelector('button')?.textContent?.trim()).toBe('Try again');
  });

  describe('the store', () => {
    it("is the shell route's, not the root's", async () => {
      await open();

      expect(() => TestBed.inject(AdminStore)).toThrow();
      expect(store()).toBeInstanceOf(AdminStore);
    });

    it('is emptied when the area is left, and read afresh when it is entered again', async () => {
      await open();
      const first = store();
      expect(first.users().length).toBeGreaterThan(0);

      await harness.navigateByUrl('/elsewhere');
      expect(first.loaded()).toBe(false);
      expect(first.users()).toEqual([]);

      await harness.navigateByUrl('/admin/roles');
      // Entering again shows the loading state, not what the area held before, until the new answer is in.
      expect(page()).toBeNull();
      answerLoad(http, adminData({ users: [] }));
      await settle();
      expect(page()).not.toBeNull();
    });
  });
});
