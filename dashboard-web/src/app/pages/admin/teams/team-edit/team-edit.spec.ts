import { ChangeDetectionStrategy, Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import {
  ADMINISTRATOR_ID,
  AdminData,
  OPERATOR_ID,
  PERMISSIONS,
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
import teamRequest from '../../../../testing/contracts/admin/team.request.json';
import { problem } from '../../../../testing/http-error';
import { AdminStore } from '../../admin.store';
import { TeamEdit } from './team-edit';

@Component({
  selector: 'app-list-stub',
  template: 'the teams',
  changeDetection: ChangeDetectionStrategy.Eager,
})
class ListStub {}

const ROUTES: Routes = [
  {
    path: 'admin/teams',
    children: [
      { path: '', component: ListStub },
      { path: 'new', component: TeamEdit },
      { path: ':id', component: TeamEdit },
    ],
  },
];

const LAST_ADMIN =
  'This change would leave no enabled user who can manage access for all saga types.';
const SIGNED_IN = {
  id: 'u-admin',
  username: 'admin',
  displayName: 'Administrator',
  mustChangePassword: false,
};

const ADMIN = adminUser({
  id: 'u-admin',
  username: 'admin',
  displayName: 'Administrator',
  grants: [grant(ADMINISTRATOR_ID)],
});
const ALICE = adminUser({ id: 'u-alice', username: 'alice', displayName: 'Alice Example' });
const BOB = adminUser({ id: 'u-bob', username: 'Bob', displayName: 'Robert Builder' });
const CAROL = adminUser({ id: 'u-carol', username: 'carol', displayName: 'Carol Example' });
/** The member of team.request.json. */
const DANA = adminUser({ id: teamRequest.memberIds[0], username: 'dana', displayName: 'Dana' });

const PAYMENTS = team({
  id: 't-pay',
  name: 'Payments',
  description: 'The payments on-call rota.',
  memberIds: ['u-alice'],
  grants: [grant(OPERATOR_ID, ['OrderSaga'])],
});
const OPERATIONS = team({
  id: 't-ops',
  name: 'Operations',
  description: null,
  memberIds: [],
  grants: [],
});
/** A team whose grant is what makes the signed-in administrator one: the last one. */
const ADMINS = team({
  id: 't-admins',
  name: 'Admins',
  description: null,
  memberIds: ['u-alice'],
  grants: [grant(ADMINISTRATOR_ID)],
});

/** Users out of order on purpose: the page lists them by username, ignoring case. */
const teams = (...some: ReturnType<typeof team>[]) =>
  adminData({ users: [CAROL, BOB, ADMIN, ALICE], teams: some });

describe('TeamEdit', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let store: AdminStore;

  async function open(url: string, data: AdminData = teams(PAYMENTS, OPERATIONS)): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(ROUTES),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideAuthMock(createAuthMock({ user: SIGNED_IN })),
        AdminStore,
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AdminStore);
    const loading = store.load();
    answerLoad(http, data);
    await loading;
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    await settle();
  }

  afterEach(() => http.verify());

  /** Lets promises finish and the model write its values to the inputs, then renders. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve));
    harness.detectChanges();
    await harness.fixture.whenStable();
  }

  const el = () => harness.routeNativeElement as HTMLElement;
  const url = () => TestBed.inject(Router).url;
  const focused = () => document.activeElement;
  const name = () => el().querySelector<HTMLInputElement>('#team-name')!;
  const description = () => el().querySelector<HTMLTextAreaElement>('#team-description')!;
  const filter = () => el().querySelector<HTMLInputElement>('#team-members-filter')!;
  const message = (id: string) => el().querySelector(`#${id}`)?.textContent?.trim();
  const banner = (selector = '.banner--error') => el().querySelector<HTMLElement>(selector);
  const button = (label: string) =>
    Array.from(el().querySelectorAll<HTMLElement>('button, a.btn')).find(
      (b) => b.textContent?.trim() === label,
    );
  const submit = () => el().querySelector<HTMLButtonElement>('form.card button[type="submit"]')!;
  /** Whether a button says it is unavailable (aria-disabled, as the confirm button does: it stays focusable). */
  const unavailable = (target: HTMLElement | undefined) =>
    target?.getAttribute('aria-disabled') === 'true';

  function type(target: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    target.value = value;
    target.dispatchEvent(new Event('input'));
    harness.detectChanges();
  }

  function check(target: HTMLInputElement, on: boolean): void {
    target.checked = on;
    target.dispatchEvent(new Event('change'));
    harness.detectChanges();
  }

  async function save(): Promise<void> {
    submit().click();
    await settle();
  }

  /** The write the page sent. Returns the request before it is answered. */
  function write(method: string, path: string): TestRequest {
    const req = http.expectOne(path);
    expect(req.request.method).toBe(method);
    return req;
  }

  /** Answers the three reads that follow a change, once the store has asked for them. */
  async function reloaded(data: AdminData = teams(PAYMENTS, OPERATIONS)): Promise<void> {
    await settle();
    answerReload(http, data);
    await settle();
  }

  const refuse = (req: TestRequest, status: number, body: object | null) =>
    req.flush(body, { status, statusText: 'Refused' });
  const noContent = (req: TestRequest) =>
    req.flush(null, { status: 204, statusText: 'No Content' });

  // The members.
  const boxes = () => Array.from(el().querySelectorAll<HTMLInputElement>('.member-list input'));
  const memberLabel = (box: HTMLInputElement) =>
    box.closest('label')!.textContent!.replace(/\s+/g, ' ').trim();
  const box = (username: string) => boxes().find((b) => memberLabel(b).split(' ')[0] === username)!;
  const listed = () => boxes().map((b) => [memberLabel(b), b.checked]);
  const count = () => el().querySelector('.member-tools [role="status"]')?.textContent?.trim();
  const selected = () => message('team-members-selected');

  // The grants editor, through the page.
  const editor = () => el().querySelector('app-grants-editor') as HTMLElement;
  const grantGroups = () => Array.from(editor().querySelectorAll<HTMLElement>('.grant'));
  const grantSelect = (i: number) => grantGroups()[i].querySelector<HTMLSelectElement>('select')!;
  const grantRoles = () => grantGroups().map((_, i) => grantSelect(i).value);
  const grantBox = (i: number, typeName: string) =>
    Array.from(grantGroups()[i].querySelectorAll<HTMLInputElement>('.types input')).find(
      (b) => b.closest('label')?.textContent?.trim() === typeName,
    )!;
  const scopeRadio = (i: number, label: string) =>
    Array.from(grantGroups()[i].querySelectorAll<HTMLLabelElement>('.scope label'))
      .find((l) => l.textContent?.trim() === label)!
      .querySelector('input')!;

  async function addGrant(roleId: string, scope: 'all' | string[]): Promise<void> {
    const before = grantGroups().length;
    el().querySelector<HTMLButtonElement>('app-grants-editor .add button')!.click();
    await settle();
    const select = grantSelect(before);
    select.value = roleId;
    select.dispatchEvent(new Event('change'));
    await settle();
    if (scope === 'all') {
      scopeRadio(before, 'All saga types').click();
      await settle();
    } else {
      for (const typeName of scope) {
        check(grantBox(before, typeName), true);
      }
      await settle();
    }
  }

  async function removeGrant(i: number): Promise<void> {
    Array.from(grantGroups()[i].querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Remove')!
      .click();
    await settle();
  }

  /** The access preview as lines: the saga types, and each permission with what grants it. */
  function preview() {
    return Array.from(el().querySelectorAll('app-access-summary tbody tr')).map((row) => ({
      scope: row.querySelector('td')?.textContent?.trim(),
      permissions: Array.from(row.querySelectorAll('li')).map((li) => [
        li.querySelector('.chip')?.textContent?.trim(),
        li.querySelector('.origins')?.textContent?.trim(),
      ]),
    }));
  }

  describe('a new team', () => {
    it('starts empty: a name, a description, nobody in it, no grants, and Create team', async () => {
      await open('/admin/teams/new');

      expect(el().querySelector('h2')?.textContent).toBe('New team');
      expect(name().value).toBe('');
      expect(description().value).toBe('');
      expect(boxes().filter((b) => b.checked)).toHaveLength(0);
      expect(grantGroups()).toHaveLength(0);
      expect(button('Create team')).toBeDefined();
      expect((button('Cancel') as HTMLAnchorElement).getAttribute('href')).toBe('/admin/teams');
      // Nothing that belongs to a team that exists: there is nothing yet for a save to replace.
      expect(button('Delete team')).toBeUndefined();
      expect(el().querySelector('#team-save-hint')).toBeNull();
      expect(submit().getAttribute('aria-describedby')).toBeNull();
    });

    it('sets the attributes of the fields: the API limits, and the global textarea primitive', async () => {
      await open('/admin/teams/new');

      expect(name().getAttribute('maxlength')).toBe('64');
      expect(name().getAttribute('autocomplete')).toBe('off');
      expect(description().getAttribute('maxlength')).toBe('256');
      expect(description().classList).toContain('input');
      expect(el().querySelector('label[for="team-name"]')?.textContent).toBe('Name');
      expect(el().querySelector('label[for="team-description"]')?.textContent).toBe('Description');
    });

    describe('Create team', () => {
      async function fixtureDraft(): Promise<void> {
        type(name(), teamRequest.name);
        type(description(), teamRequest.description);
        check(box('dana'), true);
        await addGrant(OPERATOR_ID, teamRequest.grants[0].sagaTypes);
      }

      const withDana = () => adminData({ users: [ADMIN, ALICE, DANA], teams: [] });

      it('sends exactly team.request.json for the fixture draft, then goes to the list', async () => {
        await open('/admin/teams/new', withDana());
        await fixtureDraft();

        await save();
        const post = write('POST', '/api/admin/teams');
        expect(post.request.body).toEqual(teamRequest);
        post.flush(team({ id: 't-new' }));
        await reloaded(withDana());

        expect(url()).toBe('/admin/teams');
      });

      it('sends exactly the members of the API request and nothing about the users', async () => {
        await open('/admin/teams/new', withDana());
        await fixtureDraft();

        await save();

        const post = write('POST', '/api/admin/teams');
        expect(Object.keys(post.request.body as object).sort()).toEqual([
          'description',
          'grants',
          'memberIds',
          'name',
        ]);
        post.flush(team());
        await reloaded(withDana());
        // Membership is written through the team only: no user is written.
        http.expectNone((r) => r.url.includes('/users') && r.method !== 'GET');
      });

      it('trims the name and the description', async () => {
        await open('/admin/teams/new');
        type(name(), '  Payments ');
        type(description(), '  The rota.  ');

        await save();

        const post = write('POST', '/api/admin/teams');
        expect(post.request.body).toMatchObject({ name: 'Payments', description: 'The rota.' });
        post.flush(team());
        await reloaded();
      });

      it('sends a blank description as an empty one, and a team with nobody and no grants with empty lists', async () => {
        await open('/admin/teams/new');
        type(name(), 'Empty');
        type(description(), '   ');

        await save();

        const post = write('POST', '/api/admin/teams');
        expect(post.request.body).toEqual({
          name: 'Empty',
          description: '',
          memberIds: [],
          grants: [],
        });
        post.flush(team());
        await reloaded();
      });

      it('keeps the line breaks of the description', async () => {
        await open('/admin/teams/new');
        type(name(), 'Notes');
        type(description(), 'First line\nSecond line');

        await save();

        const post = write('POST', '/api/admin/teams');
        expect(post.request.body).toMatchObject({ description: 'First line\nSecond line' });
        post.flush(team());
        await reloaded();
      });

      it('sends a grant for every saga type with no saga type', async () => {
        await open('/admin/teams/new');
        type(name(), 'Everyone');
        await addGrant(VIEWER_ID, 'all');

        await save();

        const post = write('POST', '/api/admin/teams');
        expect((post.request.body as { grants: unknown }).grants).toEqual([grant(VIEWER_ID)]);
        post.flush(team());
        await reloaded();
      });

      it('shows the work: the button is disabled and says so, and a second submit sends nothing', async () => {
        await open('/admin/teams/new');
        type(name(), 'Payments');

        await save();
        expect(submit().disabled).toBe(true);
        expect(submit().textContent?.trim()).toBe('Saving…');
        el()
          .querySelector('form.card')!
          .dispatchEvent(new Event('submit', { cancelable: true }));
        await settle();

        write('POST', '/api/admin/teams').flush(team());
        await reloaded();
      });

      it('goes back to the list for Cancel, with nothing sent', async () => {
        await open('/admin/teams/new');
        type(name(), 'Payments');

        (button('Cancel') as HTMLAnchorElement).click();
        await settle();

        expect(url()).toBe('/admin/teams');
        http.expectNone((r) => r.method !== 'GET');
      });

      it('keeps the form busy until the page has gone, so it cannot be used twice', async () => {
        await open('/admin/teams/new');
        type(name(), 'Payments');
        const navigate = vi
          .spyOn(TestBed.inject(Router), 'navigateByUrl')
          .mockImplementation(() => new Promise<boolean>(() => undefined));

        await save();
        write('POST', '/api/admin/teams').flush(team());
        await reloaded();

        expect(navigate).toHaveBeenCalledWith('/admin/teams');
        expect(submit().disabled).toBe(true);
        expect(submit().textContent?.trim()).toBe('Saving…');
      });
    });

    describe('the checks made before anything is sent', () => {
      it('asks for a name, under its field, and sends nothing', async () => {
        await open('/admin/teams/new');

        await save();

        expect(message('team-name-error')).toBe('Enter a name.');
        http.expectNone((r) => r.method !== 'GET');
      });

      it('marks the field: aria-invalid, aria-describedby, and the message beside it, with the focus', async () => {
        await open('/admin/teams/new');

        await save();

        expect(name().getAttribute('aria-invalid')).toBe('true');
        expect(name().getAttribute('aria-describedby')).toBe('team-name-error');
        expect(el().querySelector('#team-name-error')?.classList).toContain('field-error');
        expect(focused()).toBe(name());
      });

      it('shows nothing before the first save, and follows the draft after it', async () => {
        await open('/admin/teams/new');
        expect(el().querySelector('.field-error')).toBeNull();

        await save();
        type(name(), 'Payments');
        await settle();

        expect(el().querySelector('.field-error')).toBeNull();
      });

      it('treats a blank name as none', async () => {
        await open('/admin/teams/new');
        type(name(), '    ');

        await save();

        expect(message('team-name-error')).toBe('Enter a name.');
      });

      it('refuses a name and a description over the API limits, which maxlength only stops from typing', async () => {
        await open('/admin/teams/new');
        type(name(), 'n'.repeat(65));
        type(description(), 'd'.repeat(257));

        await save();

        expect(message('team-name-error')).toBe('Use at most 64 characters.');
        expect(message('team-description-error')).toBe('Use at most 256 characters.');
        expect(description().getAttribute('aria-invalid')).toBe('true');
        expect(description().getAttribute('aria-describedby')).toBe('team-description-error');
        expect(focused()).toBe(name());
        http.expectNone((r) => r.method !== 'GET');
      });

      it('counts the description as the API does, after trimming', async () => {
        await open('/admin/teams/new');
        type(name(), 'Payments');
        type(description(), `  ${'d'.repeat(256)}  `);

        await save();

        const post = write('POST', '/api/admin/teams');
        expect((post.request.body as { description: string }).description).toHaveLength(256);
        post.flush(team());
        await reloaded();
      });

      it('moves focus to the description when only it is wrong', async () => {
        await open('/admin/teams/new');
        type(name(), 'Payments');
        type(description(), 'd'.repeat(257));

        await save();

        expect(focused()).toBe(description());
      });
    });
  });

  describe('an existing team', () => {
    it('is filled in from the store: the name as the heading and a field, the description, the members and the grants', async () => {
      await open('/admin/teams/t-pay');

      expect(el().querySelector('h2')?.textContent).toBe('Payments');
      expect(name().value).toBe('Payments');
      expect(description().value).toBe('The payments on-call rota.');
      expect(listed()).toEqual([
        ['admin Administrator', false],
        ['alice Alice Example', true],
        ['Bob Robert Builder', false],
        ['carol Carol Example', false],
      ]);
      expect(grantGroups()).toHaveLength(1);
      expect(grantSelect(0).value).toBe(OPERATOR_ID);
      expect(grantBox(0, 'OrderSaga').checked).toBe(true);
      expect(button('Save')).toBeDefined();
      expect(button('Delete team')).toBeDefined();
    });

    it('shows an empty description as an empty field', async () => {
      await open('/admin/teams/t-ops');

      expect(description().value).toBe('');
    });

    it('says beside Save that it replaces the members and the access with what is shown, and describes Save by it', async () => {
      await open('/admin/teams/t-pay');

      expect(message('team-save-hint')).toBe(
        "Saving replaces the team's members and access with what is shown here.",
      );
      expect(el().querySelector('#team-save-hint')?.classList).toContain('field-hint');
      expect(submit().getAttribute('aria-describedby')).toBe('team-save-hint');
    });

    it('is not called Cancel: the question of Delete team has a Cancel of its own, and the way back is "Back to the teams"', async () => {
      await open('/admin/teams/t-pay');

      expect(button('Cancel')).toBeUndefined();
      expect((button('Back to the teams') as HTMLAnchorElement).getAttribute('href')).toBe(
        '/admin/teams',
      );
    });

    describe('Save', () => {
      const withDana = () =>
        adminData({
          users: [ADMIN, ALICE, DANA],
          teams: [team({ id: 't-pay', name: 'Old', description: 'Old words.', memberIds: [] })],
        });

      it('sends exactly team.request.json for the fixture draft, as a PUT', async () => {
        await open('/admin/teams/t-pay', withDana());
        type(name(), teamRequest.name);
        type(description(), teamRequest.description);
        check(box('dana'), true);
        await addGrant(OPERATOR_ID, teamRequest.grants[0].sagaTypes);

        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect(put.request.body).toEqual(teamRequest);
        put.flush({ ...teamRequest, id: 't-pay' });
        await reloaded(withDana());
      });

      it('sends the whole team when nothing was changed: the PUT replaces members and grants, so it carries them all', async () => {
        const full = team({
          id: 't-pay',
          name: 'Payments',
          description: 'The rota.',
          memberIds: ['u-alice', 'u-carol'],
          grants: [grant(OPERATOR_ID, ['OrderSaga']), grant(VIEWER_ID)],
        });
        await open('/admin/teams/t-pay', teams(full));

        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect(put.request.body).toEqual({
          name: 'Payments',
          description: 'The rota.',
          memberIds: ['u-alice', 'u-carol'],
          grants: [grant(OPERATOR_ID, ['OrderSaga']), grant(VIEWER_ID)],
        });
        put.flush(full);
        await reloaded(teams(full));
      });

      it('sends the members in the order the list shows them, whatever order the boxes were ticked in', async () => {
        await open('/admin/teams/t-ops');
        check(box('carol'), true);
        check(box('Bob'), true);
        check(box('admin'), true);

        await save();

        const put = write('PUT', '/api/admin/teams/t-ops');
        expect((put.request.body as { memberIds: string[] }).memberIds).toEqual([
          'u-admin',
          'u-bob',
          'u-carol',
        ]);
        put.flush(OPERATIONS);
        await reloaded();
      });

      it('sends the members that are no longer ticked out, and the ticked ones in', async () => {
        await open('/admin/teams/t-pay');
        check(box('alice'), false);
        check(box('Bob'), true);

        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect((put.request.body as { memberIds: string[] }).memberIds).toEqual(['u-bob']);
        put.flush(PAYMENTS);
        await reloaded();
      });

      it('sends an empty member list for a team emptied of its members', async () => {
        await open('/admin/teams/t-pay');
        check(box('alice'), false);

        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect((put.request.body as { memberIds: string[] }).memberIds).toEqual([]);
        put.flush(PAYMENTS);
        await reloaded();
      });

      it('sends the grants as drafted: one removed, one added for every saga type', async () => {
        await open('/admin/teams/t-pay');
        await removeGrant(0);
        await addGrant(VIEWER_ID, 'all');

        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect((put.request.body as { grants: unknown }).grants).toEqual([grant(VIEWER_ID)]);
        put.flush(PAYMENTS);
        await reloaded();
      });

      it('sends a grant for every saga type with no saga type, even when the record it started from names some', async () => {
        await open(
          '/admin/teams/t-pay',
          teams(
            team({
              ...PAYMENTS,
              grants: [{ roleId: VIEWER_ID, allSagaTypes: true, sagaTypes: ['Leftover'] }],
            }),
          ),
        );

        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect((put.request.body as { grants: unknown }).grants).toEqual([grant(VIEWER_ID)]);
        put.flush(PAYMENTS);
        await reloaded();
      });

      it('shows what the API answered and says Saved, in a live region that was there before', async () => {
        await open('/admin/teams/t-pay');
        const region = el().querySelector('form.card')!.previousElementSibling!;
        expect(region.getAttribute('role')).toBe('status');
        expect(region.textContent?.trim()).toBe('');
        type(name(), '  Payments EU ');

        await save();
        const stored = team({ ...PAYMENTS, name: 'Payments EU' });
        write('PUT', '/api/admin/teams/t-pay').flush(stored);
        await reloaded(teams(stored, OPERATIONS));

        expect(url()).toBe('/admin/teams/t-pay');
        expect(name().value).toBe('Payments EU');
        expect(el().querySelector('h2')?.textContent).toBe('Payments EU');
        expect(banner('.banner--success')?.textContent).toContain('Saved.');
        expect(banner('.banner--success')?.closest('[role="status"]')).toBe(region);
      });

      it.each([
        ['the name is typed in', () => type(name(), 'Other')],
        ['the description is typed in', () => type(description(), 'Other')],
        ['a member is ticked', () => check(box('Bob'), true)],
        ['a saga type is ticked', () => check(grantBox(0, 'PaymentSaga'), true)],
        ['a grant is removed', () => undefined],
      ])('takes Saved away as soon as the draft changes again: %s', async (when, change) => {
        await open('/admin/teams/t-pay');
        await save();
        write('PUT', '/api/admin/teams/t-pay').flush(PAYMENTS);
        await reloaded();
        expect(banner('.banner--success')).not.toBeNull();

        if (when === 'a grant is removed') await removeGrant(0);
        else change();
        await settle();

        expect(banner('.banner--success')).toBeNull();
      });

      it('keeps what is being typed when the store reads the lists again behind the page', async () => {
        await open('/admin/teams/t-pay');
        type(name(), 'Half typed');
        check(box('Bob'), true);

        const other = store.saveTeam(null, {
          name: 'Zed',
          description: '',
          memberIds: [],
          grants: [],
        });
        write('POST', '/api/admin/teams').flush(team({ id: 't-zed', name: 'Zed' }));
        await settle();
        answerReload(http, teams(PAYMENTS, OPERATIONS, team({ id: 't-zed', name: 'Zed' })));
        await other;
        await settle();

        expect(name().value).toBe('Half typed');
        expect(box('Bob').checked).toBe(true);
      });
    });

    describe('what is typed while a request runs', () => {
      it.each([
        ['while the save is on its way', 'put'],
        ['while the lists are read again after it', 'reload'],
      ] as const)('is not overwritten by the answer to a save, %s', async (_when, moment) => {
        await open('/admin/teams/t-pay');
        type(name(), 'First');
        await save();
        const put = write('PUT', '/api/admin/teams/t-pay');
        const typeMore = () => {
          type(name(), 'Second');
          check(box('Bob'), true);
        };
        if (moment === 'put') typeMore();
        put.flush({ ...PAYMENTS, name: 'First' });
        await settle();
        if (moment === 'reload') typeMore();
        answerReload(http, teams({ ...PAYMENTS, name: 'First' }, OPERATIONS));
        await settle();

        // The user's draft stands, and is not announced as saved: it is not what the API stored.
        expect(name().value).toBe('Second');
        expect(box('Bob').checked).toBe(true);
        expect(banner('.banner--success')).toBeNull();
        expect(submit().disabled).toBe(false);
      });

      it('is replaced by what the API stored when nothing was typed meanwhile', async () => {
        await open('/admin/teams/t-pay');
        type(name(), '  First ');
        check(box('Bob'), true);
        await save();
        const stored = { ...PAYMENTS, name: 'First', memberIds: ['u-alice', 'u-bob'] };
        write('PUT', '/api/admin/teams/t-pay').flush(stored);
        await reloaded(teams(stored, OPERATIONS));

        expect(name().value).toBe('First');
        expect(banner('.banner--success')).not.toBeNull();
      });

      it('is not overwritten when only the grants were changed meanwhile', async () => {
        await open('/admin/teams/t-pay');
        await save();
        const put = write('PUT', '/api/admin/teams/t-pay');

        await addGrant(VIEWER_ID, 'all');
        put.flush(PAYMENTS);
        await reloaded();

        expect(grantRoles()).toEqual([OPERATOR_ID, VIEWER_ID]);
        expect(banner('.banner--success')).toBeNull();
      });

      it('is not overwritten when only the description was typed meanwhile', async () => {
        await open('/admin/teams/t-pay');
        await save();
        const put = write('PUT', '/api/admin/teams/t-pay');

        type(description(), 'Typed meanwhile');
        put.flush(PAYMENTS);
        await reloaded();

        expect(description().value).toBe('Typed meanwhile');
        expect(banner('.banner--success')).toBeNull();
      });
    });

    describe('the team the page shows', () => {
      it('is the one the URL names, when the router reuses the page for another', async () => {
        await open('/admin/teams/t-pay');
        type(name(), 'typed over');
        type(filter(), 'ali');
        const page = harness.routeDebugElement!.componentInstance;

        await harness.navigateByUrl('/admin/teams/t-ops');
        await settle();

        expect(harness.routeDebugElement!.componentInstance).toBe(page);
        expect(el().querySelector('h2')?.textContent).toBe('Operations');
        expect(name().value).toBe('Operations');
        expect(description().value).toBe('');
        expect(boxes().filter((b) => b.checked)).toHaveLength(0);
        expect(grantGroups()).toHaveLength(0);
        // The filter belonged to the other team's page.
        expect(filter().value).toBe('');
        expect(boxes()).toHaveLength(4);
      });

      it('does not apply the answer to a save to the team the page shows by then', async () => {
        await open('/admin/teams/t-pay');
        await save();
        const put = write('PUT', '/api/admin/teams/t-pay');

        await harness.navigateByUrl('/admin/teams/t-ops');
        await settle();
        put.flush(PAYMENTS);
        await reloaded();

        expect(name().value).toBe('Operations');
        expect(banner('.banner--success')).toBeNull();
      });
    });
  });

  describe('a team that is not there', () => {
    it('says "This no longer exists" and links back to the list, with no form, and focuses the link', async () => {
      await open('/admin/teams/nobody');

      const alert = banner('.banner--warning[role="alert"]')!;
      expect(alert.textContent).toContain('This no longer exists');
      expect(alert.querySelector('a')?.getAttribute('href')).toBe('/admin/teams');
      expect(alert.querySelector('a')?.textContent).toBe('Back to the teams');
      expect(el().querySelector('form')).toBeNull();
      expect(focused()).toBe(alert.querySelector('a'));
    });

    it('says so when the team is deleted by someone else while the page is open', async () => {
      await open('/admin/teams/t-pay');
      expect(el().querySelector('form')).not.toBeNull();

      const reading = store.deleteTeam('t-pay');
      noContent(write('DELETE', '/api/admin/teams/t-pay'));
      await settle();
      answerReload(http, teams(OPERATIONS));
      await reading;
      await settle();

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
    });
  });

  describe('the members', () => {
    it('are a checkbox per user, by username ignoring case, each with the display name, in a fieldset with a legend', async () => {
      await open('/admin/teams/t-ops');

      const fieldset = el().querySelector('fieldset.members')!;
      expect(fieldset.querySelector('legend')?.textContent).toBe('Members');
      expect(listed().map(([text]) => text)).toEqual([
        'admin Administrator',
        'alice Alice Example',
        'Bob Robert Builder',
        'carol Carol Example',
      ]);
      expect(boxes().every((b) => b.closest('fieldset') === fieldset)).toBe(true);
      expect(boxes().every((b) => b.type === 'checkbox')).toBe(true);
    });

    it('tick and untick, and the count of the ones ticked says so', async () => {
      await open('/admin/teams/t-ops');
      expect(selected()).toBe('No members selected.');

      check(box('Bob'), true);
      expect(selected()).toBe('1 member selected.');
      check(box('carol'), true);
      expect(selected()).toBe('2 members selected.');
      check(box('Bob'), false);
      expect(selected()).toBe('1 member selected.');
      expect(listed().filter(([, on]) => on)).toEqual([['carol Carol Example', true]]);
    });

    it('mark a disabled user with a "Disabled" chip inside the label, so that the name of the box says it', async () => {
      await open(
        '/admin/teams/t-ops',
        adminData({
          users: [ADMIN, ALICE, adminUser({ ...BOB, isEnabled: false }), CAROL],
          teams: [OPERATIONS],
        }),
      );

      const chips = (username: string) =>
        Array.from(box(username).closest('label')!.querySelectorAll('.chip')).map((chip) =>
          chip.textContent?.trim(),
        );
      expect(chips('Bob')).toEqual(['Disabled']);
      expect(memberLabel(box('Bob'))).toBe('Bob Robert Builder Disabled');
      expect(chips('alice')).toEqual([]);
      expect(chips('carol')).toEqual([]);
      expect(box('Bob').closest('label')!.querySelector('.chip')?.classList).toContain(
        'chip--muted',
      );
    });

    it('describe the fieldset by that count', async () => {
      await open('/admin/teams/t-ops');

      expect(el().querySelector('fieldset.members')!.getAttribute('aria-describedby')).toBe(
        'team-members-selected',
      );
    });

    it('say so when there is no user to choose', async () => {
      await open('/admin/teams/new', adminData({ users: [], teams: [] }));

      expect(boxes()).toHaveLength(0);
      expect(el().querySelector('.member-list')?.textContent).toContain('There are no users yet.');
      expect(count()).toBe('0 users');
    });

    it('add the user that a change elsewhere reads in, and keep what is ticked', async () => {
      await open('/admin/teams/t-ops');
      check(box('Bob'), true);

      const adding = store.saveUser(null, {
        username: 'dave',
        displayName: 'Dave',
        password: 'a long enough password',
        mustChangePassword: true,
        grants: [],
      });
      write('POST', '/api/admin/users').flush(
        adminUser({ id: 'u-dave', username: 'dave', displayName: 'Dave' }),
      );
      await settle();
      answerReload(
        http,
        adminData({
          users: [
            ADMIN,
            ALICE,
            BOB,
            CAROL,
            adminUser({ id: 'u-dave', username: 'dave', displayName: 'Dave' }),
          ],
          teams: [PAYMENTS, OPERATIONS],
        }),
      );
      await adding;
      await settle();

      expect(boxes().map(memberLabel)).toContain('dave Dave');
      expect(box('Bob').checked).toBe(true);
    });

    describe('the filter', () => {
      it('is a labelled search box inside the fieldset', async () => {
        await open('/admin/teams/t-ops');

        expect(filter().type).toBe('search');
        expect(el().querySelector('label[for="team-members-filter"]')?.textContent).toBe(
          'Filter users',
        );
        expect(filter().closest('fieldset')).toBe(el().querySelector('fieldset.members'));
      });

      it.each([
        ['a username', 'ali', ['alice']],
        ['a username, ignoring case', 'BOB', ['Bob']],
        ['a display name', 'builder', ['Bob']],
        ['text around which there are blanks', '  carol  ', ['carol']],
        ['a few users', 'example', ['alice', 'carol']],
      ])('keeps the users that match %s', async (_what, text, expected) => {
        await open('/admin/teams/t-ops');

        type(filter(), text);

        expect(boxes().map((b) => memberLabel(b).split(' ')[0])).toEqual(expected);
      });

      it('does not match the id', async () => {
        await open('/admin/teams/t-ops');

        type(filter(), 'u-alice');

        expect(boxes()).toHaveLength(0);
      });

      it('says how many are left of how many, in a live region that is always there', async () => {
        await open('/admin/teams/t-ops');
        const region = el().querySelector('.member-tools [role="status"]')!;
        expect(count()).toBe('4 users');

        type(filter(), 'example');

        expect(el().querySelector('.member-tools [role="status"]')).toBe(region);
        expect(count()).toBe('2 of 4 users');

        type(filter(), '');
        expect(count()).toBe('4 users');
      });

      it('says no user matches, and shows them all again when it is cleared', async () => {
        await open('/admin/teams/t-ops');

        type(filter(), 'nobody');

        expect(boxes()).toHaveLength(0);
        expect(el().querySelector('.member-list')?.textContent?.trim()).toBe(
          'No user matches “nobody”.',
        );
        expect(count()).toBe('0 of 4 users');

        type(filter(), '');
        expect(boxes()).toHaveLength(4);
      });

      it('never changes who is in the team: the members it hides stay ticked, and are sent', async () => {
        await open('/admin/teams/t-pay');
        expect(box('alice').checked).toBe(true);

        type(filter(), 'carol');
        check(box('carol'), true);
        expect(boxes()).toHaveLength(1);
        expect(selected()).toBe('2 members selected.');
        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect((put.request.body as { memberIds: string[] }).memberIds).toEqual([
          'u-alice',
          'u-carol',
        ]);
        put.flush(PAYMENTS);
        await reloaded();
        // Saved, and the filter is the page's own: it stays.
        expect(filter().value).toBe('carol');
      });

      it('does not take Saved away once it has been said: filtering is not a change of the draft', async () => {
        await open('/admin/teams/t-pay');
        type(name(), '  First ');
        await save();
        write('PUT', '/api/admin/teams/t-pay').flush({ ...PAYMENTS, name: 'First' });
        await reloaded(teams({ ...PAYMENTS, name: 'First' }, OPERATIONS));
        expect(banner('.banner--success')).not.toBeNull();

        type(filter(), 'ali');
        await settle();

        expect(banner('.banner--success')).not.toBeNull();
      });

      it('does not keep the answer to a save from re-seeding the draft and saying Saved when it is typed meanwhile', async () => {
        await open('/admin/teams/t-pay');
        type(name(), '  First ');
        await save();
        const put = write('PUT', '/api/admin/teams/t-pay');

        type(filter(), 'ali');
        put.flush({ ...PAYMENTS, name: 'First' });
        await reloaded(teams({ ...PAYMENTS, name: 'First' }, OPERATIONS));

        // The draft is what the API stored (the name trimmed), and it is announced as saved.
        expect(name().value).toBe('First');
        expect(banner('.banner--success')).not.toBeNull();
        expect(filter().value).toBe('ali');
      });

      it('does not submit the form when Enter is pressed in it', async () => {
        await open('/admin/teams/t-pay');
        const enter = new KeyboardEvent('keydown', {
          key: 'Enter',
          cancelable: true,
          bubbles: true,
        });

        filter().dispatchEvent(enter);
        await settle();

        expect(enter.defaultPrevented).toBe(true);
        http.expectNone((r) => r.method !== 'GET');
      });
    });

    describe('Enter on a checkbox', () => {
      const enter = () =>
        new KeyboardEvent('keydown', { key: 'Enter', cancelable: true, bubbles: true });

      it('does not submit the form, which would replace the whole team', async () => {
        await open('/admin/teams/t-pay');
        const event = enter();

        box('alice').dispatchEvent(event);
        await settle();

        expect(event.defaultPrevented).toBe(true);
        http.expectNone((r) => r.method !== 'GET');
      });

      it('does not submit the form from the row of a member the lists do not know either', async () => {
        await open(
          '/admin/teams/t-pay',
          teams(team({ ...PAYMENTS, memberIds: ['u-alice', 'u-gone'] }), OPERATIONS),
        );
        const event = enter();

        boxes().at(-1)!.dispatchEvent(event);
        await settle();

        expect(event.defaultPrevented).toBe(true);
        http.expectNone((r) => r.method !== 'GET');
      });

      it('leaves Space alone: it is what ticks a box', async () => {
        await open('/admin/teams/t-pay');
        const space = new KeyboardEvent('keydown', { key: ' ', cancelable: true, bubbles: true });

        box('alice').dispatchEvent(space);

        expect(space.defaultPrevented).toBe(false);
      });
    });

    describe('a member that no user list knows', () => {
      const withStranger = () =>
        teams(team({ ...PAYMENTS, memberIds: ['u-alice', 'u-gone'] }), OPERATIONS);

      it('is shown, ticked and labelled with its id, after the users, so that nothing is dropped unseen', async () => {
        await open('/admin/teams/t-pay', withStranger());

        const last = boxes().at(-1)!;
        expect(last.checked).toBe(true);
        expect(last.closest('label')?.querySelector('code')?.textContent).toBe('u-gone');
        expect(last.closest('label')?.textContent).toContain('No user has this id any more');
        expect(selected()).toBe('2 members selected.');
      });

      it('is sent with the rest, after the users, and the API says if it refuses it', async () => {
        await open('/admin/teams/t-pay', withStranger());
        check(box('Bob'), true);

        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect((put.request.body as { memberIds: string[] }).memberIds).toEqual([
          'u-alice',
          'u-bob',
          'u-gone',
        ]);
        refuse(put, 400, problem('validation', 'x', { 'memberIds[2]': ['No user has this id.'] }));
        await reloaded(withStranger());

        expect(message('team-members-error')).toBe('No user has this id.');
        expect(boxes().at(-1)!.checked).toBe(true);
      });

      it('is not said to be "no user that matches" under the filter, nor "no users yet" when there are none', async () => {
        await open('/admin/teams/t-pay', withStranger());

        type(filter(), 'nobody');

        expect(boxes()).toHaveLength(1);
        expect(el().querySelector('.member-list')?.textContent).not.toContain('No user matches');
      });

      it('is not said to be "no users yet" when the user list is empty and the team still names a member', async () => {
        await open(
          '/admin/teams/t-pay',
          adminData({ users: [], teams: [team({ ...PAYMENTS, memberIds: ['u-gone'] })] }),
        );

        expect(boxes()).toHaveLength(1);
        expect(el().querySelector('.member-list')?.textContent).not.toContain(
          'There are no users yet.',
        );
      });

      it('is removed from the team by unticking it', async () => {
        await open('/admin/teams/t-pay', withStranger());

        check(boxes().at(-1)!, false);
        await settle();
        expect(boxes()).toHaveLength(4);
        await save();

        const put = write('PUT', '/api/admin/teams/t-pay');
        expect((put.request.body as { memberIds: string[] }).memberIds).toEqual(['u-alice']);
        put.flush(PAYMENTS);
        await reloaded();
      });

      it('shows up when a user is deleted while the page is open, and the draft keeps the id', async () => {
        await open('/admin/teams/t-pay');
        expect(boxes()).toHaveLength(4);

        const deleting = store.deleteUser('u-alice');
        noContent(write('DELETE', '/api/admin/users/u-alice'));
        await settle();
        answerReload(http, adminData({ users: [ADMIN, BOB, CAROL], teams: [PAYMENTS] }));
        await deleting;
        await settle();

        expect(boxes().map(memberLabel)).toContain(
          'u-alice No user has this id any more. The team keeps it until you untick it, and the API refuses to save a team that holds it.',
        );
        expect(selected()).toBe('1 member selected.');
      });
    });
  });

  describe('the grants', () => {
    it('blocks Save on a scoped grant with no saga type, says so with the grant, and moves focus to its saga types', async () => {
      await open('/admin/teams/t-ops');
      type(name(), 'Operations');
      el().querySelector<HTMLButtonElement>('app-grants-editor .add button')!.click();
      await settle();
      expect(editor().textContent).toContain('Pick at least one saga type');

      await save();

      http.expectNone((r) => r.method !== 'GET');
      expect(editor().textContent).toContain('Pick at least one saga type');
      expect(focused()).toBe(grantBox(0, 'OrderSaga'));
    });

    it('adds a name typed in an exact-name box and not added when Save is used, and stops Save when it cannot be added', async () => {
      await open('/admin/teams/t-pay');
      const box = grantGroups()[0].querySelector<HTMLInputElement>('input.input')!;
      type(box, 'x'.repeat(201));

      await save();

      http.expectNone((r) => r.method !== 'GET');
      expect(grantGroups()[0].querySelector('.custom .field-error')?.textContent).toBe(
        'Use at most 200 characters.',
      );
      expect(focused()).toBe(box);

      type(box, '  ShippingSaga ');
      await save();

      const put = write('PUT', '/api/admin/teams/t-pay');
      expect((put.request.body as { grants: unknown }).grants).toEqual([
        grant(OPERATOR_ID, ['OrderSaga', 'ShippingSaga']),
      ]);
      put.flush(PAYMENTS);
      await reloaded(teams(PAYMENTS, OPERATIONS));
    });

    it('says a grant ignores access.manage when it is scoped, by the catalogue the page passes', async () => {
      await open('/admin/teams/t-ops');

      await addGrant(ADMINISTRATOR_ID, ['OrderSaga']);

      expect(editor().textContent).toContain('access.manage is ignored in a scoped grant');
    });

    it('is sent once a saga type is picked', async () => {
      await open('/admin/teams/t-ops');
      await addGrant(OPERATOR_ID, []);
      await save();
      http.expectNone((r) => r.method !== 'GET');

      check(grantBox(0, 'PaymentSaga'), true);
      await save();

      const put = write('PUT', '/api/admin/teams/t-ops');
      expect((put.request.body as { grants: unknown }).grants).toEqual([
        grant(OPERATOR_ID, ['PaymentSaga']),
      ]);
      put.flush(OPERATIONS);
      await reloaded();
    });

    it('offers a custom role in the select, and shows what it holds in the preview', async () => {
      const support = role();
      await open(
        '/admin/teams/t-ops',
        adminData({
          users: [ADMIN],
          teams: [team({ ...OPERATIONS, grants: [grant(support.id, ['OrderSaga'])] })],
        }),
      );

      expect(Array.from(grantSelect(0).options).map((o) => o.textContent?.trim())).toContain(
        'Support',
      );
      expect(grantSelect(0).value).toBe(support.id);
      expect(preview()[0].permissions[0]).toEqual(['View sagas', 'team Operations: Support']);
    });
  });

  describe('what the API refuses', () => {
    async function filled(): Promise<void> {
      await open('/admin/teams/new');
      type(name(), 'Payments');
      check(box('alice'), true);
      check(box('Bob'), true);
      await addGrant(OPERATOR_ID, ['OrderSaga']);
      await addGrant(VIEWER_ID, 'all');
      await save();
    }

    it('puts the messages of a 400 under their fields, the members ones with the members and the grants ones with their grant, keeps the draft and focuses the first', async () => {
      await filled();

      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'The request is not valid.', {
          name: ['Enter 1 to 64 characters, with no control characters.'],
          description: ['Use at most 256 characters.'],
          'memberIds[1]': ['This user is already a member.'],
          'grants[1].roleId': ['No role has this id.'],
          'grants[0].sagaTypes': ["'OrderSaga' is listed twice."],
        }),
      );
      await reloaded();

      expect(message('team-name-error')).toContain('Enter 1 to 64 characters');
      expect(message('team-description-error')).toBe('Use at most 256 characters.');
      expect(message('team-members-error')).toBe('This user is already a member.');
      const rows = grantGroups().map((g) => g.textContent?.replace(/\s+/g, ' '));
      expect(rows[0]).toContain("'OrderSaga' is listed twice.");
      expect(rows[0]).not.toContain('No role has this id.');
      expect(rows[1]).toContain('No role has this id.');
      expect(rows[1]).not.toContain('listed twice');
      expect(banner()).toBeNull();
      // The draft is untouched.
      expect(name().value).toBe('Payments');
      expect(listed().filter(([, on]) => on)).toHaveLength(2);
      expect(grantRoles()).toEqual([OPERATOR_ID, VIEWER_ID]);
      expect(focused()).toBe(name());
      expect(url()).toBe('/admin/teams/new');
    });

    it('marks the members fieldset and describes it by the message', async () => {
      await filled();

      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', { 'memberIds[0]': ['No user has this id.'] }),
      );
      await reloaded();

      expect(el().querySelector('fieldset.members')!.getAttribute('aria-describedby')).toBe(
        'team-members-selected team-members-error',
      );
      expect(el().querySelector('#team-members-error')?.classList).toContain('field-error');
    });

    it('moves focus to the filter of the members when they are the only thing wrong, the first checkbox being possibly hidden', async () => {
      await filled();
      type(filter(), 'carol');

      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', { 'memberIds[0]': ['No user has this id.'] }),
      );
      await reloaded();

      expect(focused()).toBe(filter());
    });

    // The focus is put somewhere else before the answer: that it ends up on the grant is the page's doing, not
    // where it happened to be (the grants editor focuses the new grant's role when a grant is added).
    it("moves focus to the grant's role when its role is the only thing wrong, from wherever the focus was", async () => {
      await filled();
      name().focus();
      expect(focused()).toBe(name());

      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', { 'grants[1].roleId': ['No role has this id.'] }),
      );
      await settle();

      expect(focused()).toBe(grantSelect(1));
    });

    it("moves focus to the grant's saga types when they are the only thing wrong", async () => {
      await filled();
      name().focus();

      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', {
          'grants[0].sagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
        }),
      );
      await settle();

      expect(focused()).toBe(grantBox(0, 'OrderSaga'));
    });

    it('reads the lists again after a 400 about a member, so that a user deleted meanwhile is labelled instead of left a bare id', async () => {
      await open('/admin/teams/t-pay');
      await save();

      refuse(
        write('PUT', '/api/admin/teams/t-pay'),
        400,
        problem('validation', 'x', { 'memberIds[0]': ['No user has this id.'] }),
      );
      await settle();
      // Alice was deleted since the lists were read: the answer to the reads says so.
      answerReload(http, adminData({ users: [ADMIN, BOB, CAROL], teams: [PAYMENTS, OPERATIONS] }));
      await settle();

      expect(message('team-members-error')).toBe('No user has this id.');
      const last = boxes().at(-1)!;
      expect(last.checked).toBe(true);
      expect(last.closest('label')?.querySelector('code')?.textContent).toBe('u-alice');
      expect(boxes()).toHaveLength(4);
    });

    it('does not read the lists again for a 400 that is not about the members', async () => {
      await open('/admin/teams/t-pay');
      await save();

      refuse(
        write('PUT', '/api/admin/teams/t-pay'),
        400,
        problem('validation', 'x', {
          name: ['Bad.'],
          'grants[0].roleId': ['No role has this id.'],
        }),
      );
      await settle();

      http.expectNone('/api/admin/users');
      http.expectNone('/api/admin/teams');
    });

    it('says a message once, whichever members it is about', async () => {
      await filled();

      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', {
          'memberIds[0]': ['No user has this id.'],
          'memberIds[1]': ['No user has this id.'],
        }),
      );
      await reloaded();

      expect(message('team-members-error')).toBe('No user has this id.');
    });

    it('sends the draft again when Save is used again with nothing changed since a 400: the old messages do not block it', async () => {
      await filled();
      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', { name: ['Bad.'] }),
      );
      await settle();
      expect(message('team-name-error')).toBe('Bad.');

      await save();

      const post = write('POST', '/api/admin/teams');
      expect(message('team-name-error')).toBeUndefined();
      post.flush(team());
      await reloaded();
    });

    it("ends a field's message when that field changes, and leaves the others", async () => {
      await filled();
      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', {
          name: ['Bad.'],
          description: ['Worse.'],
          'memberIds[0]': ['No user has this id.'],
        }),
      );
      await reloaded();

      type(name(), 'Payments 2');
      expect(message('team-name-error')).toBeUndefined();
      expect(message('team-description-error')).toBe('Worse.');
      expect(message('team-members-error')).toBe('No user has this id.');

      check(box('carol'), true);
      expect(message('team-members-error')).toBeUndefined();
      expect(message('team-description-error')).toBe('Worse.');

      type(description(), 'Better');
      expect(message('team-description-error')).toBeUndefined();
    });

    it('ends the messages about the grants as soon as the grants change', async () => {
      await filled();
      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', { 'grants[1].roleId': ['No role has this id.'] }),
      );
      await settle();
      expect(editor().textContent).toContain('No role has this id.');

      await removeGrant(0);

      expect(editor().textContent).not.toContain('No role has this id.');
    });

    it('lists the messages of a path it has no field for in the banner, which takes the focus: nothing else does', async () => {
      await filled();
      name().focus();

      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', { teamIds: ['Not a member of the request.'] }),
      );
      await settle();

      expect(banner('.banner--error[role="alert"]')?.textContent).toContain(
        'Not a member of the request.',
      );
      expect(focused()).toBe(banner('.banner--error[role="alert"]'));
    });

    it('keeps the focus on the field when a path with no field comes with one that has', async () => {
      await filled();
      description().focus();

      refuse(
        write('POST', '/api/admin/teams'),
        400,
        problem('validation', 'x', { teamIds: ['Not a member of the request.'], name: ['Bad.'] }),
      );
      await settle();

      expect(focused()).toBe(name());
    });

    // The banner is at the top of a page that may be long, and a screen reader user may be anywhere in it.
    it.each([
      ['a last-administrator refusal', 409, problem('last_administrator', LAST_ADMIN)],
      ['another rule of the API', 409, problem('role_in_use', 'Some rule says no.')],
      ['a lost permission', 403, problem('forbidden', 'x')],
      ['a server error', 500, problem('x', 'server words')],
    ])(
      'moves focus to the banner for %s, from wherever the focus was',
      async (_what, status, body) => {
        await filled();
        description().focus();
        expect(focused()).toBe(description());

        refuse(write('POST', '/api/admin/teams'), status, body);
        // The store reads the lists again after a 409, and after nothing else.
        if (status === 409) await reloaded();
        else await settle();

        const alert = banner('.banner--error[role="alert"]')!;
        expect(alert.getAttribute('tabindex')).toBe('-1');
        expect(focused()).toBe(alert);
        expect(name().value).toBe('Payments');
      },
    );

    it('shows a taken name as a banner with the server detail, marks the name and focuses it', async () => {
      await filled();

      refuse(
        write('POST', '/api/admin/teams'),
        409,
        problem(
          'name_taken',
          "A team named 'Payments' already exists; names are compared ignoring case.",
        ),
      );
      await reloaded();

      const alert = el().querySelector('#team-failure')!;
      expect(alert.textContent).toContain("A team named 'Payments' already exists");
      expect(alert.getAttribute('role')).toBe('alert');
      expect(name().getAttribute('aria-invalid')).toBe('true');
      expect(name().getAttribute('aria-describedby')).toBe('team-failure');
      expect(focused()).toBe(name());
      expect(name().value).toBe('Payments');
      expect(url()).toBe('/admin/teams/new');
      expect(submit().disabled).toBe(false);
    });

    it('ends the mark on the name when it is changed', async () => {
      await filled();
      refuse(
        write('POST', '/api/admin/teams'),
        409,
        problem('name_taken', "A team named 'Payments' already exists."),
      );
      await reloaded();

      type(name(), 'Payments 2');

      expect(name().getAttribute('aria-invalid')).toBeNull();
      expect(name().getAttribute('aria-describedby')).toBeNull();
    });

    it('keeps the draft and says why for a last-administrator refusal when the grant that makes the administrator is removed', async () => {
      await open('/admin/teams/t-admins', teams(ADMINS, PAYMENTS));
      type(name(), 'Admins renamed');
      await removeGrant(0);
      await save();

      refuse(
        write('PUT', '/api/admin/teams/t-admins'),
        409,
        problem('last_administrator', LAST_ADMIN),
      );
      await reloaded(teams(ADMINS, PAYMENTS));

      const alert = banner('.banner--error[role="alert"]')!;
      expect(alert.textContent).toContain(LAST_ADMIN);
      expect(alert.textContent).toContain(
        'Give another enabled user an all-saga-types grant whose role includes access.manage, then try again.',
      );
      expect(name().value).toBe('Admins renamed');
      expect(grantGroups()).toHaveLength(0);
      expect(banner('.banner--success')).toBeNull();
      expect(submit().disabled).toBe(false);
    });

    it('keeps the draft for a last-administrator refusal when the administrator is taken out of the team', async () => {
      await open('/admin/teams/t-admins', teams(ADMINS));
      check(box('alice'), false);
      await save();

      refuse(
        write('PUT', '/api/admin/teams/t-admins'),
        409,
        problem('last_administrator', LAST_ADMIN),
      );
      await reloaded(teams(ADMINS));

      expect(banner('.banner--error[role="alert"]')?.textContent).toContain(LAST_ADMIN);
      expect(box('alice').checked).toBe(false);
    });

    it('says "This no longer exists" for a 404 on save, with the way back, and no form', async () => {
      await open('/admin/teams/t-pay');
      await save();

      refuse(write('PUT', '/api/admin/teams/t-pay'), 404, { title: 'Not found' });
      await settle();
      // The store reads the lists again after a 404, so the lists stop showing what is gone.
      answerReload(http, teams(OPERATIONS));
      await settle();

      const alert = banner('.banner--warning[role="alert"]')!;
      expect(alert.textContent).toContain('This no longer exists');
      expect(alert.querySelector('a')?.getAttribute('href')).toBe('/admin/teams');
      expect(el().querySelector('form')).toBeNull();
      expect(focused()).toBe(alert.querySelector('a'));
    });

    it('says "This no longer exists" for a 404 on save even while the lists still show the team: the API has said it', async () => {
      await open('/admin/teams/t-pay');
      await save();

      refuse(write('PUT', '/api/admin/teams/t-pay'), 404, { title: 'Not found' });
      await reloaded(teams(PAYMENTS, OPERATIONS));

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
      expect(el().querySelector('form')).toBeNull();
    });

    it.each([
      [403, 'You no longer have permission to manage access.'],
      [500, 'HTTP 500'],
    ])('says a %i in its own words and keeps the draft', async (status, text) => {
      await open('/admin/teams/t-pay');
      type(name(), 'Changed');
      check(box('Bob'), true);
      await save();

      refuse(write('PUT', '/api/admin/teams/t-pay'), status, problem('x', 'server words'));
      await settle();

      expect(banner('.banner--error[role="alert"]')?.textContent).toContain(text);
      expect(name().value).toBe('Changed');
      expect(box('Bob').checked).toBe(true);
      expect(name().getAttribute('aria-invalid')).toBeNull();
      expect(submit().disabled).toBe(false);
    });
  });

  describe('the effective access', () => {
    it('labels the permissions with the names the API gives them, not the built-in ones', async () => {
      const permissions = PERMISSIONS.map((p) =>
        p.key === 'sagas.view' ? { ...p, name: 'See sagas' } : p,
      );
      await open('/admin/teams/t-pay', { ...teams(PAYMENTS, OPERATIONS), permissions });

      const labels = preview().flatMap((row) => row.permissions.map(([label]) => label));
      expect(labels).toContain('See sagas');
      expect(labels).not.toContain('View sagas');
    });

    it('is what the grants confer to each member, with the team as the origin of each permission', async () => {
      await open('/admin/teams/t-pay');

      expect(preview()).toEqual([
        {
          scope: 'OrderSaga',
          permissions: [
            ['View sagas', 'team Payments: Operator'],
            ['View saga data', 'team Payments: Operator'],
            ['Retry sagas', 'team Payments: Operator'],
          ],
        },
      ]);
    });

    it('is about the team, not the person looking and not a user', async () => {
      await open('/admin/teams/t-pay');

      const summary = el().querySelector('app-access-summary')!;
      expect(summary.querySelector('caption')?.textContent).toContain('What the team grants');
      expect(summary.textContent).toContain('What members may do');
      expect(summary.textContent).not.toContain('you');
      expect(summary.textContent).not.toContain('user');
    });

    it('follows the draft, saved or not: a grant added, a saga type ticked, a grant removed, the name typed', async () => {
      await open('/admin/teams/t-ops');
      expect(el().querySelector('app-access-summary')?.textContent).toContain(
        'This team would grant no permissions.',
      );
      expect(el().querySelector('app-access-summary table')).toBeNull();

      await addGrant(VIEWER_ID, 'all');
      expect(preview()).toEqual([
        {
          scope: 'All saga types',
          permissions: [
            ['View sagas', 'team Operations: Viewer'],
            ['View saga data', 'team Operations: Viewer'],
          ],
        },
      ]);

      type(name(), 'Ops');
      expect(preview()[0].permissions[0]).toEqual(['View sagas', 'team Ops: Viewer']);

      await addGrant(OPERATOR_ID, ['OrderSaga']);
      expect(preview().map((row) => row.scope)).toEqual(['All saga types', 'OrderSaga']);

      await removeGrant(0);
      expect(preview().map((row) => row.scope)).toEqual(['OrderSaga']);
      http.expectNone((r) => r.method !== 'GET');
    });

    it('names an unnamed team, instead of leaving the origin with a hole', async () => {
      await open('/admin/teams/new');
      await addGrant(VIEWER_ID, 'all');

      expect(preview()[0].permissions[0]).toEqual(['View sagas', 'team (unnamed): Viewer']);
    });

    it('does not count access.manage in a scoped grant, and says the grant ignores it', async () => {
      await open('/admin/teams/t-ops');
      await addGrant(ADMINISTRATOR_ID, ['OrderSaga']);

      expect(preview()).toEqual([
        {
          scope: 'OrderSaga',
          permissions: [
            ['View sagas', 'team Operations: Administrator'],
            ['View saga data', 'team Operations: Administrator'],
            ['Retry sagas', 'team Operations: Administrator'],
          ],
        },
      ]);
      expect(editor().textContent).toContain('access.manage is ignored in a scoped grant');
    });

    it('counts access.manage in a grant for every saga type', async () => {
      await open('/admin/teams/t-ops');
      await addGrant(ADMINISTRATOR_ID, 'all');

      expect(preview()[0].permissions.map(([permission]) => permission)).toEqual([
        'View sagas',
        'View saga data',
        'Retry sagas',
        'Manage access',
      ]);
    });
  });

  describe('Delete team', () => {
    it('asks first, and deletes only on yes, then goes to the list', async () => {
      await open('/admin/teams/t-pay');

      button('Delete team')!.click();
      await settle();
      expect(el().querySelector('.confirm-prompt')?.textContent).toBe(
        'Delete the team Payments? Its members lose the access it gives them. This cannot be undone.',
      );
      http.expectNone((r) => r.method === 'DELETE');

      button('Yes, delete')!.click();
      await settle();
      noContent(write('DELETE', '/api/admin/teams/t-pay'));
      await reloaded(teams(OPERATIONS));

      expect(url()).toBe('/admin/teams');
    });

    it('is offered on a team that exists only, whatever the team holds', async () => {
      await open('/admin/teams/t-admins', teams(ADMINS));

      expect(unavailable(button('Delete team'))).toBe(false);
      expect(button('Delete team')!.getAttribute('aria-describedby')).toBeNull();
    });

    it('sends nothing on Cancel, which is the question\'s own: the page\'s way back is "Back to the teams"', async () => {
      await open('/admin/teams/t-pay');

      button('Delete team')!.click();
      await settle();
      expect(
        Array.from(el().querySelectorAll('button, a.btn')).filter(
          (b) => b.textContent?.trim() === 'Cancel',
        ),
      ).toHaveLength(1);
      expect(button('Back to the teams')).toBeDefined();
      button('Cancel')!.click();
      await settle();

      expect(el().querySelector('.confirm-prompt')).toBeNull();
      http.expectNone((r) => r.method === 'DELETE');
      expect(url()).toBe('/admin/teams/t-pay');
    });

    it('shows the API refusal of a last administrator in the banner, keeps the draft and stays', async () => {
      await open('/admin/teams/t-admins', teams(ADMINS));
      type(name(), 'Typed meanwhile');
      button('Delete team')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      refuse(
        write('DELETE', '/api/admin/teams/t-admins'),
        409,
        problem('last_administrator', LAST_ADMIN),
      );
      await reloaded(teams(ADMINS));

      const alert = banner('.banner--error[role="alert"]')!;
      expect(alert.textContent).toContain(LAST_ADMIN);
      expect(alert.textContent).toContain('Give another enabled user an all-saga-types grant');
      expect(url()).toBe('/admin/teams/t-admins');
      expect(name().value).toBe('Typed meanwhile');
      expect(unavailable(button('Delete team'))).toBe(false);
      // The question was at the bottom of the page and the banner is at the top: the focus goes to the banner.
      expect(focused()).toBe(alert);
    });

    it('says a lost permission for a 403', async () => {
      await open('/admin/teams/t-pay');
      button('Delete team')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      refuse(write('DELETE', '/api/admin/teams/t-pay'), 403, problem('forbidden', 'x'));
      await settle();

      expect(banner('.banner--error[role="alert"]')?.textContent).toContain(
        'You no longer have permission to manage access.',
      );
      expect(el().querySelector('form')).not.toBeNull();
    });

    it('says "This no longer exists" for a 404', async () => {
      await open('/admin/teams/t-pay');
      button('Delete team')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      refuse(write('DELETE', '/api/admin/teams/t-pay'), 404, { title: 'Not found' });
      await settle();
      answerReload(http, teams(OPERATIONS));
      await settle();

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
    });

    it('says "This no longer exists" for a 404 even while the lists still show the team', async () => {
      await open('/admin/teams/t-pay');
      button('Delete team')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      refuse(write('DELETE', '/api/admin/teams/t-pay'), 404, { title: 'Not found' });
      await reloaded(teams(PAYMENTS, OPERATIONS));

      expect(banner('.banner--warning[role="alert"]')?.textContent).toContain(
        'This no longer exists',
      );
      expect(el().querySelector('form')).toBeNull();
    });

    it('does not turn into "This no longer exists" between the reload and the navigation of a delete', async () => {
      await open('/admin/teams/t-pay');
      const navigate = vi
        .spyOn(TestBed.inject(Router), 'navigateByUrl')
        .mockImplementation(() => new Promise<boolean>(() => undefined));

      button('Delete team')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();
      noContent(write('DELETE', '/api/admin/teams/t-pay'));
      // The lists no longer hold the team: the page is still the page of a team that is being deleted.
      await reloaded(teams(OPERATIONS));

      expect(navigate).toHaveBeenCalledWith('/admin/teams');
      expect(banner('.banner--warning')).toBeNull();
      expect(el().querySelector('form')).not.toBeNull();
      expect(el().querySelector('h2')?.textContent).toBe('Payments');
      expect(unavailable(button('Delete team'))).toBe(true);
    });
  });

  describe('one request at a time', () => {
    it('does not ask about deleting while a save runs, and the question that is open lapses', async () => {
      await open('/admin/teams/t-pay');
      button('Delete team')!.click();
      await settle();
      expect(el().querySelector('.confirm-prompt')).not.toBeNull();

      await save();

      expect(el().querySelector('.confirm-prompt')).toBeNull();
      expect(unavailable(button('Delete team'))).toBe(true);
      write('PUT', '/api/admin/teams/t-pay').flush(PAYMENTS);
      await reloaded();
      expect(unavailable(button('Delete team'))).toBe(false);
    });

    it('does not save while a delete runs', async () => {
      await open('/admin/teams/t-pay');
      button('Delete team')!.click();
      await settle();
      button('Yes, delete')!.click();
      await settle();

      expect(submit().disabled).toBe(true);
      el()
        .querySelector('form.card')!
        .dispatchEvent(new Event('submit', { cancelable: true }));
      await settle();
      http.expectNone((r) => r.method === 'PUT');

      noContent(write('DELETE', '/api/admin/teams/t-pay'));
      await reloaded(teams(OPERATIONS));
    });

    it('does not delete while a save runs, even if the delete had been confirmed before it', async () => {
      await open('/admin/teams/t-pay');
      await save();

      const page = harness.routeDebugElement!.componentInstance as unknown as {
        remove(): Promise<void>;
      };
      await page.remove();

      http.expectNone((r) => r.method === 'DELETE');
      write('PUT', '/api/admin/teams/t-pay').flush(PAYMENTS);
      await reloaded();
    });
  });

  it('has its status regions in place before there is anything to say', async () => {
    await open('/admin/teams/t-pay');

    const regions = Array.from(el().querySelectorAll('[role="status"]'));
    // The saved notice and the grants editor's have no text yet; the member count already has.
    expect(regions).toHaveLength(3);
    expect(
      regions.filter((r) => r.children.length === 0 && r.textContent?.trim() === ''),
    ).toHaveLength(2);
    expect(count()).toBe('4 users');
  });
});
