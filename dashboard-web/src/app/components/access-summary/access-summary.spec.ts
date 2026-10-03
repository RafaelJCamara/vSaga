import { TestBed } from '@angular/core/testing';
import { SessionAccess } from '../../models/auth.model';
import { AccessSummary } from './access-summary';

describe('AccessSummary', () => {
  function render(access: SessionAccess | null): HTMLElement {
    const fixture = TestBed.createComponent(AccessSummary);
    fixture.componentRef.setInput('access', access);
    fixture.detectChanges();
    return fixture.nativeElement;
  }

  const rows = (el: HTMLElement) =>
    Array.from(el.querySelectorAll('tbody tr')).map((row) => ({
      scope: row.querySelector('td')?.textContent?.trim(),
      permissions: Array.from(row.querySelectorAll('.chip')).map((chip) =>
        chip.textContent?.trim(),
      ),
    }));

  it('says so when the session holds nothing', () => {
    for (const access of [null, { permissions: [], scoped: [] }]) {
      const el = render(access);

      expect(el.querySelector('table')).toBeNull();
      expect(el.textContent).toContain('You hold no permissions yet');
    }
  });

  it('lists what is held for every saga type in one row, labelled and in the catalogue order', () => {
    const el = render({
      permissions: ['access.manage', 'sagas.retry', 'sagas.view', 'sagas.data'],
      scoped: [],
    });

    expect(rows(el)).toEqual([
      {
        scope: 'All saga types',
        permissions: ['View sagas', 'View saga data', 'Retry sagas', 'Manage access'],
      },
    ]);
    // The key is on hover, for whoever knows the permission by it.
    expect(el.querySelector('.chip')?.getAttribute('title')).toBe('sagas.view');
    expect(el.textContent).not.toContain('not listed');
  });

  it('adds a row per saga type granted on its own, with everything held there', () => {
    const el = render({
      permissions: ['sagas.view'],
      scoped: [
        { sagaType: 'OrderSaga', permissions: ['sagas.view', 'sagas.retry'] },
        { sagaType: 'PaymentSaga', permissions: ['sagas.view', 'sagas.data'] },
      ],
    });

    expect(rows(el)).toEqual([
      { scope: 'All saga types', permissions: ['View sagas'] },
      { scope: 'OrderSaga', permissions: ['View sagas', 'Retry sagas'] },
      { scope: 'PaymentSaga', permissions: ['View sagas', 'View saga data'] },
    ]);
    expect(el.textContent).not.toContain('not listed');
  });

  it('does not show access.manage in a saga type row: it is no permission on a saga type', () => {
    const el = render({
      permissions: ['sagas.view', 'access.manage'],
      scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry', 'sagas.view'] }],
    });

    expect(rows(el)).toEqual([
      { scope: 'All saga types', permissions: ['View sagas', 'Manage access'] },
      { scope: 'OrderSaga', permissions: ['View sagas', 'Retry sagas'] },
    ]);
  });

  it('says the saga types not listed are not available when every saga type holds only access.manage', () => {
    const el = render({
      permissions: ['access.manage'],
      scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }],
    });

    expect(rows(el)).toEqual([
      { scope: 'All saga types', permissions: ['Manage access'] },
      { scope: 'OrderSaga', permissions: ['View sagas'] },
    ]);
    expect(el.querySelector('.field-hint')?.textContent).toContain(
      'Saga types that are not listed are not available to you.',
    );
  });

  it('does not say it when some saga permission is held for every saga type', () => {
    const el = render({
      permissions: ['sagas.view', 'access.manage'],
      scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry', 'sagas.view'] }],
    });

    expect(el.querySelector('.field-hint')).toBeNull();
  });

  it('shows a permission held for all saga types in every saga type row too', () => {
    const el = render({
      permissions: ['sagas.view', 'sagas.data'],
      scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry', 'sagas.view'] }],
    });

    expect(rows(el)[1]).toEqual({
      scope: 'OrderSaga',
      permissions: ['View sagas', 'View saga data', 'Retry sagas'],
    });
  });

  it('shows the saga type names as written, and says that the ones not listed are not available', () => {
    const el = render({
      permissions: [],
      scoped: [{ sagaType: 'orderSaga', permissions: ['sagas.view'] }],
    });

    expect(rows(el)).toEqual([{ scope: 'orderSaga', permissions: ['View sagas'] }]);
    expect(el.querySelector('code')?.textContent).toBe('orderSaga');
    expect(el.querySelector('.field-hint')?.textContent).toContain(
      'Saga types that are not listed are not available to you.',
    );
  });

  it('shows a key the catalogue does not know as it is, after the ones it does', () => {
    const el = render({ permissions: ['sagas.purge', 'sagas.view'], scoped: [] });

    expect(rows(el)[0].permissions).toEqual(['View sagas', 'sagas.purge']);
  });

  it('describes the table to a screen reader and heads its columns', () => {
    const el = render({ permissions: ['sagas.view'], scoped: [] });

    expect(el.querySelector('caption')?.textContent).toContain('Your permissions');
    expect(Array.from(el.querySelectorAll('th[scope="col"]')).map((th) => th.textContent)).toEqual([
      'Saga types',
      'What you may do',
    ]);
  });

  it('follows the access it is given', () => {
    const fixture = TestBed.createComponent(AccessSummary);
    fixture.componentRef.setInput('access', { permissions: ['sagas.view'], scoped: [] });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.chip')).toHaveLength(1);

    fixture.componentRef.setInput('access', {
      permissions: ['sagas.view', 'sagas.retry'],
      scoped: [],
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.chip')).toHaveLength(2);
  });

  describe("as an administrator sees another user's access (the access preview)", () => {
    const ORIGINS: Record<string, string[]> = {
      'null|sagas.view': ['direct: Viewer', 'team Payments: Viewer'],
      'null|sagas.data': ['direct: Viewer'],
      'OrderSaga|sagas.view': ['direct: Viewer', 'direct: Operator'],
      'OrderSaga|sagas.data': ['direct: Viewer'],
      'OrderSaga|sagas.retry': ['direct: Operator'],
    };
    const ACCESS: SessionAccess = {
      permissions: ['sagas.view', 'sagas.data'],
      scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry'] }],
    };

    function preview(access: SessionAccess | null, withOrigins = true): HTMLElement {
      const fixture = TestBed.createComponent(AccessSummary);
      fixture.componentRef.setInput('access', access);
      fixture.componentRef.setInput('perspective', 'user');
      if (withOrigins) {
        fixture.componentRef.setInput(
          'origins',
          (sagaType: string | null, key: string) => ORIGINS[`${sagaType}|${key}`] ?? [],
        );
      }
      fixture.detectChanges();
      return fixture.nativeElement;
    }

    const lines = (el: HTMLElement) =>
      Array.from(el.querySelectorAll('tbody tr')).map((row) => ({
        scope: row.querySelector('td')?.textContent?.trim(),
        permissions: Array.from(row.querySelectorAll('li')).map((li) => [
          li.querySelector('.chip')?.textContent?.trim(),
          li.querySelector('.origins')?.textContent?.trim(),
        ]),
      }));

    it('shows each permission with what grants it, the origins of a row apart from those of another', () => {
      expect(lines(preview(ACCESS))).toEqual([
        {
          scope: 'All saga types',
          permissions: [
            ['View sagas', 'direct: Viewer; team Payments: Viewer'],
            ['View saga data', 'direct: Viewer'],
          ],
        },
        {
          scope: 'OrderSaga',
          permissions: [
            ['View sagas', 'direct: Viewer; direct: Operator'],
            ['View saga data', 'direct: Viewer'],
            ['Retry sagas', 'direct: Operator'],
          ],
        },
      ]);
    });

    it('keeps the permission key on the chip, and shows no origins without any given', () => {
      const el = preview(ACCESS, false);

      expect(el.querySelector('.origins')).toBeNull();
      expect(el.querySelector('.chips .chip')?.getAttribute('title')).toBe('sagas.view');
      expect(preview(ACCESS).querySelector('.chip')?.getAttribute('title')).toBe('sagas.view');
    });

    it('speaks of the user, not of "you"', () => {
      const el = preview(ACCESS);

      expect(el.querySelector('caption')?.textContent).toContain("The user's permissions");
      expect(el.textContent).toContain('What they may do');
      expect(el.textContent).not.toContain('you');
      expect(el.textContent).not.toContain('Your');
    });

    it('says the user would hold nothing, not that "you" hold nothing', () => {
      const el = preview({ permissions: [], scoped: [] });

      expect(el.textContent).toContain('This user would hold no permissions.');
      expect(el.textContent).not.toContain('You hold');
    });

    it('says the saga types that are not listed are not available to the user', () => {
      const el = preview({
        permissions: [],
        scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }],
      });

      expect(el.querySelector('.field-hint')?.textContent).toBe(
        'Saga types that are not listed are not available to this user.',
      );
    });

    it('still speaks of "you" by default', () => {
      expect(render({ permissions: [], scoped: [] }).textContent).toContain(
        'You hold no permissions',
      );
    });
  });

  describe('as an administrator sees what a team grants its members (the team page)', () => {
    const ACCESS: SessionAccess = {
      permissions: ['sagas.view'],
      scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry'] }],
    };

    function team(access: SessionAccess | null): HTMLElement {
      const fixture = TestBed.createComponent(AccessSummary);
      fixture.componentRef.setInput('access', access);
      fixture.componentRef.setInput('perspective', 'team');
      fixture.componentRef.setInput('origins', () => ['team Payments: Viewer']);
      fixture.detectChanges();
      return fixture.nativeElement;
    }

    it('speaks of the team and its members, not of "you" and not of a user', () => {
      const el = team(ACCESS);

      expect(el.querySelector('caption')?.textContent).toContain('What the team grants');
      expect(el.textContent).toContain('What members may do');
      expect(el.textContent).toContain('team Payments: Viewer');
      expect(el.textContent).not.toContain('you');
      expect(el.textContent).not.toContain('user');
    });

    it('says the team would grant nothing', () => {
      const el = team({ permissions: [], scoped: [] });

      expect(el.textContent).toContain('This team would grant no permissions.');
      expect(el.querySelector('table')).toBeNull();
    });

    it('says the saga types that are not listed are not granted by the team', () => {
      const el = team({
        permissions: [],
        scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }],
      });

      expect(el.querySelector('.field-hint')?.textContent).toBe(
        'Saga types that are not listed are not granted by this team.',
      );
    });
  });

  describe("given the permission catalogue (the administration pages pass the API's)", () => {
    const CATALOGUE = [
      { key: 'sagas.view', name: 'See sagas', scopable: true },
      { key: 'sagas.data', name: 'See their data', scopable: true },
      { key: 'sagas.retry', name: 'Run again', scopable: true },
      { key: 'access.manage', name: 'Administer', scopable: false },
    ];

    function withCatalogue(
      access: SessionAccess | null,
      catalogue: typeof CATALOGUE | null = CATALOGUE,
    ): HTMLElement {
      const fixture = TestBed.createComponent(AccessSummary);
      fixture.componentRef.setInput('access', access);
      fixture.componentRef.setInput('catalogue', catalogue);
      fixture.detectChanges();
      return fixture.nativeElement;
    }

    it("labels the permissions with the catalogue's names", () => {
      const el = withCatalogue({ permissions: ['sagas.view', 'sagas.retry'], scoped: [] });

      expect(rows(el)[0].permissions).toEqual(['See sagas', 'Run again']);
      // The key is still on hover.
      expect(el.querySelector('.chip')?.getAttribute('title')).toBe('sagas.view');
    });

    it("lists the permissions in the catalogue's order, not the built-in one", () => {
      const reversed = [...CATALOGUE].reverse();

      const el = withCatalogue(
        { permissions: ['sagas.view', 'sagas.data', 'sagas.retry'], scoped: [] },
        reversed,
      );

      expect(rows(el)[0].permissions).toEqual(['Run again', 'See their data', 'See sagas']);
    });

    it("keeps out of a saga type's row what the catalogue does not scope, whatever it is called", () => {
      const el = withCatalogue({
        permissions: ['sagas.view', 'access.manage'],
        scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.retry'] }],
      });

      expect(rows(el)).toEqual([
        { scope: 'All saga types', permissions: ['See sagas', 'Administer'] },
        { scope: 'OrderSaga', permissions: ['See sagas', 'Run again'] },
      ]);
    });

    it('follows the catalogue when it scopes something else: retry is then no permission on a saga type', () => {
      const retryUnscoped = CATALOGUE.map((p) =>
        p.key === 'sagas.retry' ? { ...p, scopable: false } : p,
      );

      const el = withCatalogue(
        {
          permissions: ['sagas.view', 'sagas.retry'],
          scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.data'] }],
        },
        retryUnscoped,
      );

      expect(rows(el)[1]).toEqual({
        scope: 'OrderSaga',
        permissions: ['See sagas', 'See their data'],
      });
    });

    it('keeps access.manage in a saga type row when the catalogue says it can be scoped', () => {
      const scopesEverything = CATALOGUE.map((p) => ({ ...p, scopable: true }));

      const el = withCatalogue(
        {
          permissions: ['access.manage'],
          scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }],
        },
        scopesEverything,
      );

      expect(rows(el)[1].permissions).toEqual(['See sagas', 'Administer']);
      // And holding it for every saga type is then access to a saga: the types not listed are not hidden.
      expect(el.querySelector('.field-hint')).toBeNull();
    });

    it("says the saga types not listed are not available by the catalogue's flags: only what it does not scope is held for every type", () => {
      const el = withCatalogue({
        permissions: ['access.manage'],
        scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }],
      });

      expect(el.querySelector('.field-hint')?.textContent).toContain('not listed');

      const scopesEverything = CATALOGUE.map((p) => ({ ...p, scopable: true }));
      expect(
        withCatalogue(
          {
            permissions: ['access.manage'],
            scoped: [{ sagaType: 'OrderSaga', permissions: ['sagas.view'] }],
          },
          scopesEverything,
        ).querySelector('.field-hint'),
      ).toBeNull();
    });

    it('shows a key the catalogue does not list as it is, after the ones it does', () => {
      const el = withCatalogue({ permissions: ['sagas.purge', 'sagas.view'], scoped: [] });

      expect(rows(el)[0].permissions).toEqual(['See sagas', 'sagas.purge']);
    });

    it('is the built-in four, as before, without a catalogue', () => {
      const el = withCatalogue({ permissions: ['access.manage', 'sagas.view'], scoped: [] }, null);

      expect(rows(el)[0].permissions).toEqual(['View sagas', 'Manage access']);
    });
  });
});
