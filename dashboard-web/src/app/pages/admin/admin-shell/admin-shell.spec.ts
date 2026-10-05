import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
} from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { Router, RouterLink, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { GUIDE_ANCHORS } from '../../../components/guide-overlay/guide-tours';
import { GuideService } from '../../../services/guide.service';
import { adminData, answerLoad, answerReload } from '../../../testing/admin';
import { createGuideStorage, provideGuideStorage } from '../../../testing/guide';
import { AdminStore } from '../admin.store';
import { AdminShell } from './admin-shell';

@Component({
  selector: 'app-page-stub',
  template: '<p id="page">a page</p><input id="draft" />',
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

  describe('the anchors of the guide tour', () => {
    const anchorsIn = () =>
      Array.from(el().querySelectorAll('[data-tour]'), (e) => e.getAttribute('data-tour'));

    it('marks the tabs, and each tab, once, and nothing else', async () => {
      await open('/admin/users');

      expect(anchorsIn()).toEqual([
        'admin-nav',
        'admin-nav-users',
        'admin-nav-teams',
        'admin-nav-roles',
      ]);
      expect(el().querySelector('nav.subtabs')?.getAttribute('data-tour')).toBe('admin-nav');
      expect(tabs().map((a) => [a.textContent?.trim(), a.getAttribute('data-tour')])).toEqual([
        ['Users', 'admin-nav-users'],
        ['Teams', 'admin-nav-teams'],
        ['Roles', 'admin-nav-roles'],
      ]);
    });

    it('has them while the area is still reading, since the tour waits only for the tabs', async () => {
      await harness.navigateByUrl('/admin/users');
      const reading = { page: page(), anchors: anchorsIn() };
      answerLoad(http); // before the expectations: a failing one must not leave the reads unanswered

      expect(reading.page).toBeNull();
      expect(reading.anchors).toEqual([
        'admin-nav',
        'admin-nav-users',
        'admin-nav-teams',
        'admin-nav-roles',
      ]);
    });

    it('has them when the read failed, so the tour can still point at the tabs', async () => {
      await harness.navigateByUrl('/admin/users');
      http
        .match(() => true)
        .find((r) => r.request.url === '/api/admin/users')!
        .flush({ title: 'Boom' }, { status: 500, statusText: 'Boom' });
      await settle();

      expect(el().querySelector('.banner--error')).not.toBeNull();
      expect(anchorsIn()).toContain('admin-nav');
    });

    it('uses names of the vocabulary', async () => {
      await open('/admin/users');

      for (const name of anchorsIn()) expect(GUIDE_ANCHORS).toContain(name);
    });
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
    expect(el().querySelector('[role="status"]')?.textContent).not.toContain('Loading');
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

  it('announces loading through a live region that is always there, with the text put into it', async () => {
    await harness.navigateByUrl('/admin/users');
    const region = el().querySelector('[role="status"]')!;
    expect(region.textContent).toContain('Loading');

    answerLoad(http);
    await settle();

    // The same element, emptied: a region inserted together with its text is not reliably announced.
    expect(el().querySelector('[role="status"]')).toBe(region);
    expect(region.textContent?.trim()).toBe('');
  });

  describe('when a later read of the lists fails', () => {
    /** Opens the area, types into the page, and makes a change whose reload fails. */
    async function failReload(): Promise<HTMLInputElement> {
      await open('/admin/users');
      const draft = el().querySelector<HTMLInputElement>('#draft')!;
      draft.value = 'half typed';
      draft.dispatchEvent(new Event('input'));
      const saving = store().deleteTeam('t');
      http.expectOne('/api/admin/teams/t').flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      http.expectOne('/api/admin/teams').flush([]);
      http.expectOne('/api/admin/roles').flush([]);
      http.expectOne('/api/admin/users').error(new ProgressEvent('error'));
      await saving;
      await settle();
      return draft;
    }

    it('keeps the pages and warns, with the way to read again', async () => {
      await failReload();

      expect(page()).not.toBeNull();
      const warning = el().querySelector('.banner--warning[role="alert"]');
      expect(warning?.textContent).toContain('Cannot reach');
      expect(warning?.querySelector('button')?.textContent?.trim()).toBe('Try again');
    });

    it('reads the lists again from the warning without destroying the page or what is typed in it', async () => {
      const draft = await failReload();

      el().querySelector<HTMLButtonElement>('.banner--warning button')!.click();
      await settle();
      // Only the lists are read: not the catalogue, not the saga types, and the pages stay.
      expect(store().loaded()).toBe(true);
      expect(page()).not.toBeNull();
      http.expectNone('/api/admin/permissions');
      http.expectNone('/api/saga-types');
      answerReload(http);
      await settle();

      expect(el().querySelector('.banner--warning')).toBeNull();
      expect(el().querySelector('#draft')).toBe(draft);
      expect(draft.value).toBe('half typed');
    });
  });

  describe('when the session no longer holds access.manage', () => {
    const refused = (url: string) =>
      http
        .match(() => true)
        .find((r) => r.request.url === url)!
        .flush({ code: 'forbidden' }, { status: 403, statusText: 'Forbidden' });

    it('offers the way back to the saga list instead of a Try again that cannot succeed, when a read fails', async () => {
      await harness.navigateByUrl('/admin/users');
      refused('/api/admin/users');
      await settle();

      const alert = el().querySelector('.banner--error[role="alert"]')!;
      expect(alert.textContent).toContain('You no longer have permission to manage access.');
      expect(alert.querySelector('button')).toBeNull();
      expect(alert.querySelector('a')?.getAttribute('href')).toBe('/sagas');
    });

    it('does the same on the warning after a change', async () => {
      await open('/admin/users');
      const saving = store().deleteTeam('t');
      http.expectOne('/api/admin/teams/t').flush(null, { status: 204, statusText: 'No Content' });
      await settle();
      refused('/api/admin/users');
      await saving;
      await settle();

      expect(page()).not.toBeNull();
      const warning = el().querySelector('.banner--warning[role="alert"]')!;
      expect(warning.querySelector('button')).toBeNull();
      expect(warning.querySelector('a')?.getAttribute('href')).toBe('/sagas');
    });
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

describe('AdminShell: guide mode', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let areaShown: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    areaShown = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(ROUTES),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: GuideService, useValue: { areaShown } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => http.verify());

  const el = () => harness.routeNativeElement as HTMLElement;
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve));
    harness.detectChanges();
  };
  const announced = () => areaShown.mock.calls.map((call) => call[0]);

  it('says nothing while the area is reading, and announces the administration area once its pages show', async () => {
    await harness.navigateByUrl('/admin/users');
    harness.detectChanges();
    expect(announced()).toEqual([]); // not from the constructor, and not before there is a page

    answerLoad(http);
    await settle();

    expect(el().querySelector('#page')).not.toBeNull();
    expect(announced()).toEqual(['admin']);
  });

  it('says nothing when the read failed, and announces once asking again has worked', async () => {
    await harness.navigateByUrl('/admin/users');
    http
      .match(() => true)
      .find((r) => r.request.url === '/api/admin/users')!
      .flush({ title: 'Boom' }, { status: 500, statusText: 'Boom' });
    await settle();
    expect(announced()).toEqual([]);

    el().querySelector<HTMLButtonElement>('.banner--error button')!.click();
    await settle();
    answerLoad(http);
    await settle();

    expect(announced()).toEqual(['admin']);
  });

  it('announces it again after each navigation inside the area, which is when the guide forgets the page', async () => {
    await harness.navigateByUrl('/admin/users');
    answerLoad(http);
    await settle();
    expect(announced()).toEqual(['admin']);

    await harness.navigateByUrl('/admin/roles');
    await settle();
    expect(announced()).toEqual(['admin', 'admin']);

    await harness.navigateByUrl('/admin/roles/new');
    await settle();
    expect(announced()).toEqual(['admin', 'admin', 'admin']);
  });

  it('does not announce it while a failed refresh keeps the pages on screen', async () => {
    await harness.navigateByUrl('/admin/users');
    answerLoad(http);
    await settle();
    areaShown.mockClear();
    const store = harness.routeDebugElement!.injector.get(AdminStore);

    const saving = store.deleteTeam('t');
    http.expectOne('/api/admin/teams/t').flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    http.expectOne('/api/admin/teams').flush([]);
    http.expectOne('/api/admin/roles').flush([]);
    http.expectOne('/api/admin/users').error(new ProgressEvent('error'));
    await saving;
    await settle();

    expect(el().querySelector('.banner--warning')).not.toBeNull();
    expect(announced()).toEqual([]);
  });

  it('announces nothing once it is left', async () => {
    await harness.navigateByUrl('/admin/users');
    answerLoad(http);
    await settle();
    areaShown.mockClear();

    await harness.navigateByUrl('/elsewhere');
    await settle();

    expect(announced()).toEqual([]);
  });
});

// With the real guide: the order in which the guide forgets a page and the shell announces it again.
describe('AdminShell: the guide that is really there', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let guide: GuideService;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(ROUTES),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideGuideStorage(
          createGuideStorage({ v: 1, enabled: true, seen: { admin: 1 }, hintDismissed: true }),
        ),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    guide = TestBed.inject(GuideService); // the app creates it first thing, before any page
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => http.verify());

  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve));
    harness.detectChanges();
  };

  it('is the administration area once the pages show, and still is after each navigation inside the area', async () => {
    await harness.navigateByUrl('/admin/users');
    expect(guide.area()).toBeNull(); // the route alone is not enough: nothing shown yet
    answerLoad(http);
    await settle();
    expect(guide.area()?.id).toBe('admin');

    await harness.navigateByUrl('/admin/roles');
    await settle();
    expect(guide.area()?.id).toBe('admin'); // forgotten when the navigation ended, announced again after it

    await harness.navigateByUrl('/elsewhere');
    await settle();
    expect(guide.area()).toBeNull();
  });

  it('does not ask again for a tour the overlay gave up on: the guide changing its own state is not a reason to announce', async () => {
    guide.setEnabled(false);
    guide.setEnabled(true);
    await harness.navigateByUrl('/admin/users');
    answerLoad(http);
    await settle();
    expect(guide.request()?.area.id).toBe('admin');

    guide.abandoned(); // nothing on the page could be shown
    await settle();
    await settle();

    expect(guide.request()).toBeNull();
  });

  it('asks for the tour when it was not seen, once the pages show', async () => {
    guide.setEnabled(false);
    guide.setEnabled(true); // switching Guide on counts everything as unseen again
    await harness.navigateByUrl('/admin/users');
    expect(guide.request()).toBeNull();

    answerLoad(http);
    await settle();

    expect(guide.request()?.area.id).toBe('admin');
  });
});

// Where the keyboard focus is after a navigation inside the area. jsdom's click() does not move the focus: the
// specs put it where a user's would be with focus(), and look at document.activeElement after.
@Component({
  selector: 'app-list-stub',
  imports: [RouterLink],
  template: `<h2>Users</h2>
    <a id="open" routerLink="/admin/users/u1">alice</a>`,
  changeDetection: ChangeDetectionStrategy.Eager,
})
class ListStub {}

@Component({
  selector: 'app-edit-stub',
  template: `<h2>alice</h2>
    <button id="act" type="button">Delete</button>
    <h3>Not the page heading</h3>`,
  changeDetection: ChangeDetectionStrategy.Eager,
})
class EditStub {}

@Component({
  selector: 'app-focusing-stub',
  template: `<h2>Mine</h2>
    <input id="mine" />`,
  changeDetection: ChangeDetectionStrategy.Eager,
})
class FocusingStub {
  constructor() {
    const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    afterNextRender(() => host.querySelector<HTMLInputElement>('#mine')?.focus(), {
      injector: inject(Injector),
    });
  }
}

@Component({
  selector: 'app-headless-stub',
  template: '<p>This page has no heading.</p>',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class HeadlessStub {}

const FOCUS_ROUTES: Routes = [
  {
    path: 'admin',
    children: [
      {
        path: '',
        component: AdminShell,
        providers: [AdminStore],
        children: [
          {
            path: 'users',
            children: [
              { path: '', component: ListStub },
              { path: ':id', component: EditStub },
            ],
          },
          { path: 'teams', component: HeadlessStub },
          { path: 'focusing', component: FocusingStub },
        ],
      },
    ],
  },
  { path: 'outside', component: HeadlessStub },
  { path: 'outside-too', component: HeadlessStub },
];

describe('AdminShell: the focus after a navigation inside the area', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter(FOCUS_ROUTES), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => {
    (document.activeElement as HTMLElement | null)?.blur();
    http.verify();
  });

  const el = () => harness.routeNativeElement as HTMLElement;
  const focused = () => document.activeElement;
  const heading = () => el().querySelector<HTMLElement>('router-outlet + * h2');
  const tab = (label: string) =>
    Array.from(el().querySelectorAll<HTMLAnchorElement>('nav[aria-label="Administration"] a')).find(
      (a) => a.textContent?.trim() === label,
    )!;
  const settle = async () => {
    await new Promise((resolve) => setTimeout(resolve));
    harness.detectChanges();
    await harness.fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    harness.detectChanges();
  };

  async function open(url: string): Promise<void> {
    await harness.navigateByUrl(url);
    answerLoad(http);
    await settle();
  }

  it('does not take the focus when the area is opened: nothing was followed from it', async () => {
    await open('/admin/users');

    expect(focused()).toBe(document.body);
    expect(heading()?.hasAttribute('tabindex')).toBe(false);
  });

  it('puts it on the heading of the page that has arrived when the link that was followed went with the page', async () => {
    await open('/admin/users');
    const link = el().querySelector<HTMLAnchorElement>('#open')!;
    link.focus();
    expect(focused()).toBe(link);

    link.click();
    await settle();

    expect(heading()?.textContent).toBe('alice');
    expect(focused()).toBe(heading());
    // Focusable by script only: it is not a stop of the Tab key.
    expect(heading()?.getAttribute('tabindex')).toBe('-1');
  });

  it('does the same when a page is left by a navigation of its own (a create or a delete that returns to the list)', async () => {
    await open('/admin/users/u1');
    const act = el().querySelector<HTMLButtonElement>('#act')!;
    act.focus();
    expect(focused()).toBe(act);

    await TestBed.inject(Router).navigateByUrl('/admin/users');
    await settle();

    expect(heading()?.textContent).toBe('Users');
    expect(focused()).toBe(heading());
  });

  it('puts it on the h2, the heading of the page, not on a heading below it', async () => {
    await open('/admin/users');
    el().querySelector<HTMLAnchorElement>('#open')!.focus();
    el().querySelector<HTMLAnchorElement>('#open')!.click();
    await settle();

    expect(focused()?.tagName).toBe('H2');
    expect(el().querySelector('h3')?.hasAttribute('tabindex')).toBe(false);
  });

  it('does not take the focus from a tab link that still has it', async () => {
    await open('/admin/users/u1');
    const users = tab('Users');
    users.focus();

    users.click();
    await settle();

    expect(heading()?.textContent).toBe('Users');
    expect(focused()).toBe(users);
  });

  it("does not take the focus from the guide's dialog, which holds it while a tour runs", async () => {
    await open('/admin/users');
    const dialog = document.createElement('button');
    document.body.appendChild(dialog);
    dialog.focus();
    expect(focused()).toBe(dialog);

    await TestBed.inject(Router).navigateByUrl('/admin/users/u1');
    await settle();

    expect(heading()?.textContent).toBe('alice');
    expect(focused()).toBe(dialog);
    dialog.remove();
  });

  it('does not take the focus from what the page that arrived has focused itself', async () => {
    await open('/admin/users');
    el().querySelector<HTMLAnchorElement>('#open')!.focus();

    await TestBed.inject(Router).navigateByUrl('/admin/focusing');
    await settle();

    expect(focused()).toBe(el().querySelector('#mine'));
  });

  it('leaves the focus alone, and breaks nothing, on a page with no heading', async () => {
    await open('/admin/users');
    el().querySelector<HTMLAnchorElement>('#open')!.focus();

    await TestBed.inject(Router).navigateByUrl('/admin/teams');
    await settle();

    expect(el().querySelector('h2')).toBeNull();
    expect(focused()).toBe(document.body);
  });

  it('does it for every navigation, not only the first', async () => {
    await open('/admin/users');
    const router = TestBed.inject(Router);

    for (const [url, text] of [
      ['/admin/users/u1', 'alice'],
      ['/admin/users', 'Users'],
      ['/admin/users/u2', 'alice'],
    ]) {
      el().querySelector<HTMLElement>('#open, #act')?.focus();
      await router.navigateByUrl(url);
      await settle();
      expect(heading()?.textContent, url).toBe(text);
      expect(focused(), url).toBe(heading());
    }
  });

  it('leaves nothing subscribed to the router each time the area is left, and raises nothing when navigation goes on outside it', async () => {
    const router = TestBed.inject(Router);
    const observers = () => (router.events as unknown as { observers: unknown[] }).observers.length;
    const errors: unknown[] = [];
    const onError = (event: ErrorEvent) => errors.push(event);
    window.addEventListener('error', onError);

    // The router and the test harness keep something for themselves after a first visit: what must not grow is
    // what the area adds, so a second visit leaves as many observers behind as the first.
    await open('/admin/users');
    expect(observers()).toBeGreaterThan(1);
    await router.navigateByUrl('/outside');
    await settle();
    const afterFirst = observers();
    await open('/admin/users');
    await router.navigateByUrl('/outside-too');
    await settle();
    await new Promise((resolve) => setTimeout(resolve));
    window.removeEventListener('error', onError);

    expect(observers()).toBe(afterFirst);
    expect(errors).toEqual([]);
    expect(focused()).toBe(document.body);
  });
});
