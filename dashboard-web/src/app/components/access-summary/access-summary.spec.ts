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
});
