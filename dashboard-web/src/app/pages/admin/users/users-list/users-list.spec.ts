import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import {
  AdminData,
  OPERATOR_ID,
  VIEWER_ID,
  adminData,
  adminUser,
  answerLoad,
  answerReload,
  grant,
  role,
  team,
} from '../../../../testing/admin';
import { createAuthMock, provideAuthMock } from '../../../../testing/auth-mock';
import { AdminStore } from '../../admin.store';
import { UsersList } from './users-list';

const FUTURE = '2099-01-01T00:00:00+00:00';
const PAST = '2020-01-01T00:00:00+00:00';

const ME = adminUser({
  id: 'u-me',
  username: 'admin',
  displayName: 'Administrator',
  grants: [grant(OPERATOR_ID)],
  lastSignInAtUtc: '2026-10-03T12:15:00+00:00',
});
const ALICE = adminUser({
  id: 'u-alice',
  username: 'alice',
  displayName: 'Alice Example',
  grants: [grant(OPERATOR_ID, ['OrderSaga', 'PaymentSaga'])],
});
const BOB = adminUser({
  id: 'u-bob',
  username: 'Bob',
  displayName: 'Robert Builder',
  isEnabled: false,
  mustChangePassword: true,
  lockedUntilUtc: FUTURE,
});
const CAROL = adminUser({
  id: 'u-carol',
  username: 'carol',
  displayName: 'Carol Example',
  lockedUntilUtc: PAST,
  grants: [grant(VIEWER_ID), grant(OPERATOR_ID, ['OrderSaga'])],
});

describe('UsersList', () => {
  let fixture: ComponentFixture<UsersList>;
  let http: HttpTestingController;

  async function render(overrides: Partial<AdminData> = {}): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAuthMock(createAuthMock({ user: { ...userOf(ME), id: ME.id } })),
        AdminStore,
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const store = TestBed.inject(AdminStore);
    const loading = store.load();
    answerLoad(
      http,
      adminData({
        users: [CAROL, BOB, ME, ALICE],
        teams: [
          team({ id: 't-pay', name: 'Payments', memberIds: ['u-alice', 'u-carol'] }),
          team({ id: 't-ops', name: 'Operations', memberIds: ['u-alice'] }),
        ],
        roles: adminData().roles,
        ...overrides,
      }),
    );
    await loading;
    fixture = TestBed.createComponent(UsersList);
    fixture.detectChanges();
  }

  function userOf(user: typeof ME) {
    return {
      id: user.id,
      username: user.username,
      displayName: user.displayName,
      mustChangePassword: false,
    };
  }

  afterEach(() => http.verify());

  const el = () => fixture.nativeElement as HTMLElement;
  const rows = () => Array.from(el().querySelectorAll<HTMLTableRowElement>('tbody tr'));
  const cells = (row: HTMLTableRowElement) => Array.from(row.querySelectorAll('td'));
  const names = () => rows().map((row) => cells(row)[0].querySelector('a')?.textContent);
  const count = () => el().querySelector('[role="status"]')?.textContent?.trim();

  async function filter(text: string): Promise<void> {
    const input = el().querySelector<HTMLInputElement>('#users-filter')!;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('lists the users by username, ignoring case, each username a link to the user', async () => {
    await render();

    expect(names()).toEqual(['admin', 'alice', 'Bob', 'carol']);
    const links = rows().map((row) => cells(row)[0].querySelector('a')?.getAttribute('href'));
    expect(links).toEqual([
      '/admin/users/u-me',
      '/admin/users/u-alice',
      '/admin/users/u-bob',
      '/admin/users/u-carol',
    ]);
  });

  it('heads the columns and describes the table', async () => {
    await render();

    expect(
      Array.from(el().querySelectorAll('th[scope="col"]')).map((th) => th.textContent),
    ).toEqual(['Username', 'Display name', 'Status', 'Teams', 'Access', 'Last sign-in']);
    expect(el().querySelector('caption')?.classList).toContain('sr-only');
    expect(el().querySelector('h2')?.textContent).toBe('Users');
  });

  it('shows the display name', async () => {
    await render();

    expect(rows().map((row) => cells(row)[1].textContent)).toEqual([
      'Administrator',
      'Alice Example',
      'Robert Builder',
      'Carol Example',
    ]);
  });

  it('marks the signed-in users own record', async () => {
    await render();

    expect(cells(rows()[0])[0].textContent).toContain('(you)');
    expect(cells(rows()[1])[0].textContent).not.toContain('(you)');
  });

  describe('status', () => {
    const chips = (name: string) =>
      Array.from(
        rows()
          .find((row) => cells(row)[0].textContent?.includes(name))!
          .querySelectorAll('.chip'),
      ).map((chip) => chip.textContent?.trim());

    it('shows a chip for each thing the matter with an account: Disabled, Locked, Must change password', async () => {
      await render();

      expect(chips('Bob')).toEqual(['Disabled', 'Locked', 'Must change password']);
    });

    it('shows nothing for an account with nothing the matter, and no Locked for a lockout that has ended', async () => {
      await render();

      expect(chips('alice')).toEqual([]);
      expect(chips('carol')).toEqual([]);
    });

    it('shows each chip alone when only that is the matter', async () => {
      await render({
        users: [
          adminUser({ id: 'a', username: 'a', isEnabled: false }),
          adminUser({ id: 'b', username: 'b', lockedUntilUtc: FUTURE }),
          adminUser({ id: 'c', username: 'c', mustChangePassword: true }),
        ],
      });

      expect(
        rows().map((row) => Array.from(row.querySelectorAll('.chip')).map((c) => c.textContent)),
      ).toEqual([['Disabled'], ['Locked'], ['Must change password']]);
    });
  });

  describe('teams', () => {
    it('shows the teams a user is in, each a link to the team, and none for a user in no team', async () => {
      await render();
      const teams = (i: number) =>
        Array.from(cells(rows()[i])[3].querySelectorAll('a')).map((a) => [
          a.textContent,
          a.getAttribute('href'),
        ]);

      expect(teams(1)).toEqual([
        ['Payments', '/admin/teams/t-pay'],
        ['Operations', '/admin/teams/t-ops'],
      ]);
      expect(teams(3)).toEqual([['Payments', '/admin/teams/t-pay']]);
      expect(cells(rows()[0])[3].textContent?.trim()).toBe('None');
      expect(cells(rows()[0])[3].querySelector('a')).toBeNull();
    });

    it('shows them read-only: nothing in the list can change membership', async () => {
      await render();

      expect(el().querySelectorAll('input[type="checkbox"], select')).toHaveLength(0);
    });
  });

  describe('access', () => {
    it("summarizes the user's own grants, one phrase each", async () => {
      await render();
      const access = (i: number) =>
        Array.from(cells(rows()[i])[4].querySelectorAll('div')).map((d) => d.textContent);

      expect(access(0)).toEqual(['Operator · all types']);
      expect(access(1)).toEqual(['Operator · 2 types']);
      expect(access(3)).toEqual(['Viewer · all types', 'Operator · 1 type']);
    });

    it('says a user holds nothing directly when there are no grants', async () => {
      await render();

      expect(cells(rows()[2])[4].textContent?.trim()).toBe('None directly');
    });

    it('names a custom role by its name', async () => {
      const support = role();
      await render({
        users: [adminUser({ id: 'x', username: 'x', grants: [grant(support.id, ['A'])] })],
        roles: [...adminData().roles, support],
      });

      expect(cells(rows()[0])[4].textContent?.trim()).toBe('Support · 1 type');
    });
  });

  describe('last sign-in', () => {
    it('shows it in local time, with the exact instant for machines and on hover', async () => {
      await render();

      const time = cells(rows()[0])[5].querySelector('time')!;
      expect(time.getAttribute('datetime')).toBe('2026-10-03T12:15:00+00:00');
      expect(time.getAttribute('title')).toContain('2026-10-03 12:15:00');
      // The local text depends on the zone the spec runs in (the day may be the 2nd, 3rd or 4th).
      expect(time.textContent).toMatch(/^Oct [234], 2026, \d{2}:\d{2}:\d{2}\.\d{3}$/);
    });

    it('says Never for a user who has not signed in', async () => {
      await render();

      expect(cells(rows()[1])[5].textContent?.trim()).toBe('Never');
      expect(cells(rows()[1])[5].querySelector('time')).toBeNull();
    });
  });

  describe('the filter', () => {
    it('is a labelled search box', async () => {
      await render();

      const input = el().querySelector<HTMLInputElement>('#users-filter')!;
      expect(input.type).toBe('search');
      expect(el().querySelector('label[for="users-filter"]')?.textContent).toBe('Filter users');
    });

    it.each([
      ['a username', 'ali', ['alice']],
      ['a username, ignoring case', 'BOB', ['Bob']],
      ['a display name', 'builder', ['Bob']],
      ['a team name', 'payments', ['alice', 'carol']],
      ['part of a team name', 'oper', ['alice']],
      ['text around which there are blanks', '  carol  ', ['carol']],
      ['a few users', 'example', ['alice', 'carol']],
    ])('keeps the users that match %s', async (_what, text, expected) => {
      await render();

      await filter(text);

      expect(names()).toEqual(expected);
    });

    it('does not match the access, the status or the id', async () => {
      await render();

      await filter('Operator · 2');
      expect(names()).toEqual([]);
      await filter('u-alice');
      expect(names()).toEqual([]);
    });

    it('says how many are left of how many, in a live region that is always there', async () => {
      await render();
      const region = el().querySelector('[role="status"]')!;
      expect(count()).toBe('4 total');

      await filter('example');

      expect(el().querySelector('[role="status"]')).toBe(region);
      expect(count()).toBe('2 of 4');

      await filter('');
      expect(count()).toBe('4 total');
    });

    it('says no user matches, with the table gone, and shows them all again when the filter is cleared', async () => {
      await render();

      await filter('nobody');

      expect(el().querySelector('table')).toBeNull();
      expect(el().querySelector('.empty')?.textContent?.trim()).toBe('No user matches “nobody”.');
      expect(count()).toBe('0 of 4');

      await filter('');
      expect(names()).toHaveLength(4);
      expect(el().querySelector('.empty')).toBeNull();
    });
  });

  it('offers New user, as a link to the form', async () => {
    await render();

    const link = el().querySelector<HTMLAnchorElement>('a.new-user')!;
    expect(link.textContent?.trim()).toBe('New user');
    expect(link.getAttribute('href')).toBe('/admin/users/new');
  });

  it('follows the store when a change reads the lists again', async () => {
    await render();
    expect(names()).toHaveLength(4);

    const store = TestBed.inject(AdminStore);
    const saving = store.saveUser(null, {
      username: 'dave',
      displayName: 'Dave',
      password: 'a long enough password',
      mustChangePassword: true,
      grants: [],
    });
    http.expectOne('/api/admin/users').flush(adminUser({ id: 'u-dave', username: 'dave' }));
    await Promise.resolve();
    answerReload(
      http,
      adminData({ users: [ME, ALICE, BOB, CAROL, adminUser({ id: 'u-dave', username: 'dave' })] }),
    );
    await saving;
    fixture.detectChanges();

    expect(names()).toEqual(['admin', 'alice', 'Bob', 'carol', 'dave']);
    expect(count()).toBe('5 total');
  });

  it('says there are no users yet, with no filter in the way', async () => {
    await render({ users: [] });

    expect(el().querySelector('table')).toBeNull();
    expect(el().querySelector('.empty')?.textContent?.trim()).toBe('There are no users yet.');
    expect(count()).toBe('0 total');
  });
});
