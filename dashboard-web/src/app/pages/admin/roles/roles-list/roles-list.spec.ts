import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import {
  ADMINISTRATOR_ID,
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
import { AdminStore } from '../../admin.store';
import { RolesList } from './roles-list';

describe('RolesList', () => {
  let fixture: ComponentFixture<RolesList>;
  let http: HttpTestingController;
  let store: AdminStore;

  async function create(data: AdminData = adminData()): Promise<void> {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), AdminStore],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AdminStore);
    const done = store.load();
    answerLoad(http, data);
    await done;
    fixture = TestBed.createComponent(RolesList);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  const el = () => fixture.nativeElement as HTMLElement;
  const rows = () => Array.from(el().querySelectorAll('tbody tr'));
  const cells = (row: Element) => Array.from(row.querySelectorAll('td'));
  const names = () => rows().map((row) => row.querySelector('td a')?.textContent?.trim());

  it('lists every role: the built-in ones first, then the custom ones by name', async () => {
    await create(
      adminData({
        roles: [
          role({ id: 'r-zeta', name: 'Zeta' }),
          ...adminData().roles.filter((r) => r.isBuiltIn),
          role({ id: 'r-alpha', name: 'alpha' }),
        ],
      }),
    );

    expect(names()).toEqual(['Administrator', 'Operator', 'Viewer', 'alpha', 'Zeta']);
    expect(el().querySelector('h2')?.textContent).toBe('Roles');
    expect(el().querySelector('.page-header .muted')?.textContent).toContain('5 total');
  });

  it('says whether each role is built in or custom', async () => {
    await create();

    const types = rows().map((row) => cells(row)[1].textContent?.trim());
    expect(types).toEqual(['Built-in', 'Built-in', 'Built-in', 'Custom']);
    expect(cells(rows()[0])[1].querySelector('.chip--muted')).not.toBeNull();
    expect(cells(rows()[3])[1].querySelector('.chip')).toBeNull();
  });

  it("shows each role's permissions as chips named by the catalogue, with the key on hover", async () => {
    await create();

    const chips = (row: Element) =>
      Array.from(cells(row)[2].querySelectorAll('.chip')).map((c) => [
        c.textContent?.trim(),
        c.getAttribute('title'),
      ]);
    expect(chips(rows()[2])).toEqual([
      ['View sagas', 'sagas.view'],
      ['View saga data', 'sagas.data'],
    ]);
    expect(chips(rows()[0]).map((c) => c[0])).toEqual([
      'View sagas',
      'View saga data',
      'Retry sagas',
      'Manage access',
    ]);
  });

  it('names a permission the catalogue does not know by its key', async () => {
    await create(adminData({ roles: [role({ permissions: ['sagas.view', 'sagas.export'] })] }));

    expect(
      Array.from(el().querySelectorAll('tbody .chip')).map((c) => c.textContent?.trim()),
    ).toEqual(['View sagas', 'sagas.export']);
  });

  it('shows the description under the name when there is one', async () => {
    await create(
      adminData({
        roles: [
          role({ description: 'Looks, never touches.' }),
          role({ id: 'r2', name: 'Quiet', description: null }),
        ],
      }),
    );

    expect(el().querySelectorAll('.description')).toHaveLength(1);
    expect(el().querySelector('.description')?.textContent).toBe('Looks, never touches.');
  });

  describe('used by', () => {
    it('counts the grants of users and teams that name the role', async () => {
      await create(
        adminData({
          users: [
            adminUser({ id: 'u1', grants: [grant(ADMINISTRATOR_ID), grant(OPERATOR_ID, ['A'])] }),
            adminUser({ id: 'u2', grants: [grant(OPERATOR_ID)] }),
          ],
          teams: [team({ grants: [grant(OPERATOR_ID, ['B']), grant(VIEWER_ID)] })],
        }),
      );

      const used = rows().map((row) => cells(row)[3].textContent?.trim());
      expect(used).toEqual(['Used by 1 grant', 'Used by 3 grants', 'Used by 1 grant', 'Not used']);
    });

    it('follows the store when a change is made', async () => {
      await create(adminData({ users: [], teams: [] }));
      expect(rows().map((row) => cells(row)[3].textContent?.trim())).toEqual([
        'Not used',
        'Not used',
        'Not used',
        'Not used',
      ]);

      const saving = store.saveUser('u', {
        displayName: 'U',
        isEnabled: true,
        grants: [grant(VIEWER_ID)],
      });
      http.expectOne('/api/admin/users/u').flush(adminUser());
      await new Promise((resolve) => setTimeout(resolve));
      answerReload(
        http,
        adminData({ users: [adminUser({ grants: [grant(VIEWER_ID)] })], teams: [] }),
      );
      await saving;
      fixture.detectChanges();

      expect(rows().map((row) => cells(row)[3].textContent?.trim())).toEqual([
        'Not used',
        'Not used',
        'Used by 1 grant',
        'Not used',
      ]);
    });
  });

  describe('links', () => {
    it("links each name to the role's page and offers New role", async () => {
      await create();

      const links = Array.from(el().querySelectorAll<HTMLAnchorElement>('tbody td a')).map((a) =>
        a.getAttribute('href'),
      );
      expect(links).toEqual(adminData().roles.map((r) => `/admin/roles/${r.id}`));
      expect(el().querySelector('a.btn')?.getAttribute('href')).toBe('/admin/roles/new');
      expect(el().querySelector('a.btn')?.textContent?.trim()).toBe('New role');
    });
  });

  it('describes the table to a screen reader and heads every column', async () => {
    await create();

    expect(el().querySelector('caption')?.textContent).toContain('Roles');
    expect(
      Array.from(el().querySelectorAll('th')).map((th) => [
        th.textContent?.trim(),
        th.getAttribute('scope'),
      ]),
    ).toEqual([
      ['Name', 'col'],
      ['Type', 'col'],
      ['Permissions', 'col'],
      ['In use', 'col'],
    ]);
  });
});
