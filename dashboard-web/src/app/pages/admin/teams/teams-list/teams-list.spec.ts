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
import { AdminStore } from '../../admin.store';
import { TeamsList } from './teams-list';

const PAYMENTS = team({
  id: 't-pay',
  name: 'Payments',
  description: 'The payments on-call rota.',
  memberIds: ['u-alice', 'u-bob', 'u-carol'],
  grants: [grant(OPERATOR_ID, ['OrderSaga', 'PaymentSaga']), grant(VIEWER_ID)],
});
const OPERATIONS = team({
  id: 't-ops',
  name: 'operations',
  description: null,
  memberIds: ['u-alice'],
  grants: [grant(VIEWER_ID)],
});
const EMPTY = team({
  id: 't-new',
  name: 'Zeta',
  description: null,
  memberIds: [],
  grants: [],
});

describe('TeamsList', () => {
  let fixture: ComponentFixture<TeamsList>;
  let http: HttpTestingController;

  async function render(overrides: Partial<AdminData> = {}): Promise<void> {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), AdminStore],
    });
    http = TestBed.inject(HttpTestingController);
    const store = TestBed.inject(AdminStore);
    const loading = store.load();
    answerLoad(http, adminData({ teams: [EMPTY, PAYMENTS, OPERATIONS], ...overrides }));
    await loading;
    fixture = TestBed.createComponent(TeamsList);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  const el = () => fixture.nativeElement as HTMLElement;
  const rows = () => Array.from(el().querySelectorAll<HTMLTableRowElement>('tbody tr'));
  const cells = (row: HTMLTableRowElement) => Array.from(row.querySelectorAll('td'));
  const names = () => rows().map((row) => cells(row)[0].querySelector('a')?.textContent);

  it('lists the teams by name, ignoring case, each name a link to the team', async () => {
    await render();

    expect(names()).toEqual(['operations', 'Payments', 'Zeta']);
    const links = rows().map((row) => cells(row)[0].querySelector('a')?.getAttribute('href'));
    expect(links).toEqual(['/admin/teams/t-ops', '/admin/teams/t-pay', '/admin/teams/t-new']);
  });

  it('heads the columns, describes the table and counts the teams', async () => {
    await render();

    expect(
      Array.from(el().querySelectorAll('th[scope="col"]')).map((th) => th.textContent),
    ).toEqual(['Name', 'Members', 'Access']);
    expect(el().querySelector('caption')?.classList).toContain('sr-only');
    expect(el().querySelector('h2')?.textContent).toBe('Teams');
    expect(el().querySelector('.page-header .muted')?.textContent).toBe('3 total');
  });

  it('shows the description under the name, and nothing for a team without one', async () => {
    await render();

    expect(cells(rows()[1])[0].querySelector('.description')?.textContent).toBe(
      'The payments on-call rota.',
    );
    expect(cells(rows()[0])[0].querySelector('.description')).toBeNull();
  });

  it('counts the members, with the singular for one and a word for none', async () => {
    await render();

    expect(rows().map((row) => cells(row)[1].textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      '1 member',
      '3 members',
      'No members',
    ]);
  });

  it('summarizes the grants every member holds, one phrase each, and says when there is no access', async () => {
    await render();
    const access = (i: number) =>
      Array.from(cells(rows()[i])[2].querySelectorAll('div')).map((d) => d.textContent);

    expect(access(0)).toEqual(['Viewer · all types']);
    expect(access(1)).toEqual(['Operator · 2 types', 'Viewer · all types']);
    expect(cells(rows()[2])[2].textContent?.trim()).toBe('No access');
    expect(cells(rows()[2])[2].querySelector('div')).toBeNull();
  });

  it('names a custom role by its name, and says "1 type" for one saga type', async () => {
    const support = role();
    await render({
      teams: [team({ id: 'x', name: 'X', grants: [grant(support.id, ['OrderSaga'])] })],
      roles: [...adminData().roles, support],
    });

    expect(cells(rows()[0])[2].textContent?.trim()).toBe('Support · 1 type');
  });

  it('offers New team, as a link to the form', async () => {
    await render();

    const link = el().querySelector<HTMLAnchorElement>('a.new-team')!;
    expect(link.textContent?.trim()).toBe('New team');
    expect(link.getAttribute('href')).toBe('/admin/teams/new');
  });

  it('shows nothing that changes anything: a list is for reading', async () => {
    await render();

    expect(el().querySelectorAll('input, select, button')).toHaveLength(0);
  });

  it('says there are no teams yet, with the table gone and New team still offered', async () => {
    await render({ teams: [] });

    expect(el().querySelector('table')).toBeNull();
    expect(el().querySelector('.empty')?.textContent?.trim()).toBe('There are no teams yet.');
    expect(el().querySelector('.page-header .muted')?.textContent).toBe('0 total');
    expect(el().querySelector('a.new-team')).not.toBeNull();
  });

  it('follows the store when a change reads the lists again', async () => {
    await render();
    expect(names()).toHaveLength(3);

    const store = TestBed.inject(AdminStore);
    const saving = store.saveTeam(null, {
      name: 'Alpha',
      description: '',
      memberIds: [],
      grants: [],
    });
    http.expectOne('/api/admin/teams').flush(team({ id: 't-alpha', name: 'Alpha' }));
    await Promise.resolve();
    answerReload(
      http,
      adminData({
        users: [adminUser()],
        teams: [EMPTY, PAYMENTS, OPERATIONS, team({ id: 't-alpha', name: 'Alpha' })],
      }),
    );
    await saving;
    fixture.detectChanges();

    expect(names()).toEqual(['Alpha', 'operations', 'Payments', 'Zeta']);
    expect(el().querySelector('.page-header .muted')?.textContent).toBe('4 total');
  });
});
