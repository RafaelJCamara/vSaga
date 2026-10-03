import { ChangeDetectionStrategy, Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ADMINISTRATOR_ID,
  BUILT_IN_ROLES,
  OPERATOR_ID,
  VIEWER_ID,
  grant,
  role,
} from '../../../testing/admin';
import { Grant, Role } from '../admin.model';
import { GrantsEditor, grantsBody, scopedWithoutTypes } from './grants-editor';

const SUPPORT = role();

/** The editor as a page uses it: two-way bound grants, inside the page's form. */
@Component({
  selector: 'app-host',
  imports: [GrantsEditor],
  template: `
    <form (submit)="$event.preventDefault(); submits = submits + 1">
      <app-grants-editor
        [(grants)]="grants"
        [roles]="roles()"
        [sagaTypes]="sagaTypes()"
        [errors]="errors()"
      />
    </form>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
})
class Host {
  readonly grants = signal<Grant[]>([]);
  readonly roles = signal<Role[]>([...BUILT_IN_ROLES, SUPPORT]);
  readonly sagaTypes = signal<string[]>(['OrderSaga', 'PaymentSaga']);
  readonly errors = signal<Record<string, string[]>>({});
  submits = 0;
  readonly editor = viewChild.required(GrantsEditor);
}

describe('GrantsEditor', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;

  async function render(grants: Grant[] = [], setup: (host: Host) => void = () => undefined) {
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    host.grants.set(grants);
    setup(host);
    await settle();
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const groups = () => Array.from(el().querySelectorAll<HTMLElement>('.grant'));
  const group = (i: number) => groups()[i];
  const select = (i: number) => group(i).querySelector<HTMLSelectElement>('select')!;
  const optionNames = (i: number) =>
    Array.from(select(i).options).map((o) => o.textContent?.trim());
  const radio = (i: number, label: 'All saga types' | 'Selected saga types') =>
    Array.from(group(i).querySelectorAll<HTMLLabelElement>('.scope label'))
      .find((l) => l.textContent?.trim() === label)!
      .querySelector<HTMLInputElement>('input')!;
  const boxes = (i: number) =>
    Array.from(group(i).querySelectorAll<HTMLInputElement>('.types input[type="checkbox"]'));
  const typeLabels = (i: number) => boxes(i).map((b) => b.closest('label')?.textContent?.trim());
  const ticked = (i: number) =>
    boxes(i)
      .filter((b) => b.checked)
      .map((b) => b.closest('label')?.textContent?.trim());
  const customInput = (i: number) => group(i).querySelector<HTMLInputElement>('input.input')!;
  const button = (text: string, within: ParentNode = el()) =>
    Array.from(within.querySelectorAll<HTMLButtonElement>('button')).find(
      (b) => b.textContent?.trim() === text,
    )!;
  const addGrant = () => button('Add grant');
  const text = (i: number) => group(i).textContent?.replace(/\s+/g, ' ');
  const focused = () => document.activeElement;

  async function pickRole(i: number, id: string): Promise<void> {
    select(i).value = id;
    select(i).dispatchEvent(new Event('change'));
    await settle();
  }

  async function choose(i: number, label: 'All saga types' | 'Selected saga types') {
    radio(i, label).click();
    await settle();
  }

  async function tick(i: number, name: string, on = true): Promise<void> {
    const box = boxes(i).find((b) => b.closest('label')?.textContent?.trim() === name)!;
    box.checked = on;
    box.dispatchEvent(new Event('change'));
    await settle();
  }

  async function typeName(i: number, value: string): Promise<void> {
    customInput(i).value = value;
    customInput(i).dispatchEvent(new Event('input'));
    await settle();
  }

  async function addTyped(i: number): Promise<void> {
    button('Add type', group(i)).click();
    await settle();
  }

  describe('the grants it shows', () => {
    it('has a row per grant: the role, the scope, and a checkbox per known saga type, ticked as granted', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga']), grant(VIEWER_ID)]);

      expect(groups()).toHaveLength(2);
      expect(select(0).value).toBe(OPERATOR_ID);
      expect(radio(0, 'Selected saga types').checked).toBe(true);
      expect(radio(0, 'All saga types').checked).toBe(false);
      expect(typeLabels(0)).toEqual(['OrderSaga', 'PaymentSaga']);
      expect(ticked(0)).toEqual(['OrderSaga']);
      expect(select(1).value).toBe(VIEWER_ID);
      expect(radio(1, 'All saga types').checked).toBe(true);
      // A grant for every saga type has no list of them, and no way to type one.
      expect(boxes(1)).toEqual([]);
      expect(group(1).querySelector('input.input')).toBeNull();
    });

    it('lists the saga types a grant names that are not known, after the known ones, ticked', async () => {
      await render([grant(OPERATOR_ID, ['Legacy', 'OrderSaga'])]);

      expect(typeLabels(0)).toEqual(['OrderSaga', 'PaymentSaga', 'Legacy']);
      expect(ticked(0)).toEqual(['OrderSaga', 'Legacy']);
    });

    it('says so when there are no grants, and names the saga types a grant lists in a group named for it', async () => {
      await render([]);
      expect(el().textContent).toContain('No grants yet.');

      host.grants.set([grant(OPERATOR_ID, ['OrderSaga'])]);
      await settle();

      expect(el().textContent).not.toContain('No grants yet.');
      expect(group(0).getAttribute('role')).toBe('group');
      expect(group(0).getAttribute('aria-label')).toBe('Grant 1: Operator');
      expect(group(0).querySelector('.scope legend')?.textContent).toBe('Saga types');
    });

    it('shows a role that no role has any more as an unknown one, so the select still says something', async () => {
      await render([grant('deleted-role', ['OrderSaga'])]);

      expect(optionNames(0)).toContain('Unknown role');
      expect(select(0).selectedOptions[0].textContent?.trim()).toBe('Unknown role');
      expect(group(0).getAttribute('aria-label')).toBe('Grant 1: Unknown role');
    });

    it('says when no saga type is known, and still takes one by name', async () => {
      await render([grant(OPERATOR_ID, [])], (h) => h.sagaTypes.set([]));

      expect(text(0)).toContain('No saga types are known yet');
      expect(customInput(0)).not.toBeNull();
    });
  });

  describe('one grant per role', () => {
    it("offers a role only if no other grant holds it, and always the grant's own", async () => {
      await render([grant(OPERATOR_ID, ['A']), grant(VIEWER_ID)]);

      expect(optionNames(0)).toEqual(['Administrator', 'Operator', 'Support']);
      expect(optionNames(1)).toEqual(['Administrator', 'Viewer', 'Support']);
    });

    it('lets a role go to another grant once the grant that held it has changed role', async () => {
      await render([grant(OPERATOR_ID, ['A']), grant(VIEWER_ID)]);

      await pickRole(0, SUPPORT.id);

      expect(optionNames(1)).toEqual(['Administrator', 'Operator', 'Viewer']);
      expect(host.grants().map((g) => g.roleId)).toEqual([SUPPORT.id, VIEWER_ID]);
    });

    it('adds a grant for the role that holds the fewest permissions of those no grant holds, scoped to no saga type yet', async () => {
      await render([grant(ADMINISTRATOR_ID)]);

      addGrant().click();
      await settle();

      // Operator holds three permissions, Viewer and Support two: the first of those two in the select's order.
      expect(host.grants()).toEqual([
        grant(ADMINISTRATOR_ID),
        { roleId: VIEWER_ID, allSagaTypes: false, sagaTypes: [] },
      ]);
      expect(select(1).value).toBe(VIEWER_ID);
      expect(text(1)).toContain('Pick at least one saga type');
      // The new grant is where the next action is: its role.
      expect(focused()).toBe(select(1));
    });

    it('never starts a new grant as the administrator while another role is left', async () => {
      await render([]);

      addGrant().click();
      await settle();

      expect(host.grants()[0].roleId).not.toBe(ADMINISTRATOR_ID);
    });

    it('starts the last role there is with that role, even the administrator', async () => {
      await render([grant(OPERATOR_ID), grant(VIEWER_ID), grant(SUPPORT.id)]);

      addGrant().click();
      await settle();

      expect(host.grants()[3].roleId).toBe(ADMINISTRATOR_ID);
    });

    it('cannot add a grant once every role is held, and says why', async () => {
      await render([
        grant(ADMINISTRATOR_ID),
        grant(OPERATOR_ID),
        grant(VIEWER_ID),
        grant(SUPPORT.id),
      ]);

      expect(addGrant().disabled).toBe(true);
      expect(el().textContent).toContain('Every role is already granted.');
      addGrant().click();
      await settle();
      expect(host.grants()).toHaveLength(4);
    });

    it('can add a grant again after one is removed', async () => {
      await render([
        grant(ADMINISTRATOR_ID),
        grant(OPERATOR_ID),
        grant(VIEWER_ID),
        grant(SUPPORT.id),
      ]);

      button('Remove', group(2)).click();
      await settle();

      expect(addGrant().disabled).toBe(false);
      expect(el().textContent).not.toContain('Every role is already granted.');
      addGrant().click();
      await settle();
      expect(host.grants().map((g) => g.roleId)).toEqual([
        ADMINISTRATOR_ID,
        OPERATOR_ID,
        SUPPORT.id,
        VIEWER_ID,
      ]);
    });

    it('does not say every role is granted when there are no roles at all', async () => {
      await render([], (h) => h.roles.set([]));

      expect(addGrant().disabled).toBe(true);
      expect(el().textContent).not.toContain('Every role is already granted.');
    });

    it('keeps the grant on the same row, and the scope and saga types it had, when its role changes', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga'])]);
      const before = select(0);
      before.focus();

      await pickRole(0, SUPPORT.id);

      // Not a new row: the select that has the focus is the one that was changed.
      expect(select(0)).toBe(before);
      expect(focused()).toBe(before);
      expect(host.grants()).toEqual([grant(SUPPORT.id, ['OrderSaga'])]);
    });
  });

  describe('all or selected saga types', () => {
    it('changes a scoped grant to every saga type, with no saga type named, and takes the list away', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga'])]);

      await choose(0, 'All saga types');

      expect(host.grants()).toEqual([grant(OPERATOR_ID)]);
      expect(boxes(0)).toEqual([]);
      expect(group(0).querySelector('input.input')).toBeNull();
    });

    it('changes a grant for every saga type to selected ones, with none picked yet', async () => {
      await render([grant(VIEWER_ID)]);

      await choose(0, 'Selected saga types');

      expect(host.grants()).toEqual([{ roleId: VIEWER_ID, allSagaTypes: false, sagaTypes: [] }]);
      expect(typeLabels(0)).toEqual(['OrderSaga', 'PaymentSaga']);
      expect(ticked(0)).toEqual([]);
    });

    it('does not carry the saga types of one grant over to another', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga']), grant(VIEWER_ID, ['PaymentSaga'])]);

      await choose(0, 'All saga types');

      expect(host.grants()[1]).toEqual(grant(VIEWER_ID, ['PaymentSaga']));
      expect(ticked(1)).toEqual(['PaymentSaga']);
    });

    it('groups the two radios of a grant apart from those of the others', async () => {
      await render([grant(OPERATOR_ID, ['A']), grant(VIEWER_ID)]);

      const names = [0, 1].map((i) => [
        radio(i, 'All saga types').name,
        radio(i, 'Selected saga types').name,
      ]);

      expect(names[0][0]).toBe(names[0][1]);
      expect(names[1][0]).toBe(names[1][1]);
      expect(names[0][0]).not.toBe(names[1][0]);
    });
  });

  describe('the saga types of a scoped grant', () => {
    it('ticks and unticks them, in the order they were ticked, writing a new list each time', async () => {
      const original = grant(OPERATOR_ID, []);
      await render([original]);

      await tick(0, 'PaymentSaga');
      await tick(0, 'OrderSaga');
      expect(host.grants()[0].sagaTypes).toEqual(['PaymentSaga', 'OrderSaga']);

      await tick(0, 'PaymentSaga', false);
      expect(host.grants()[0].sagaTypes).toEqual(['OrderSaga']);
      // Nothing it was given was changed in place.
      expect(original.sagaTypes).toEqual([]);
    });

    it('drops an unknown saga type from the list when it is unticked, and keeps the others', async () => {
      await render([grant(OPERATOR_ID, ['Legacy', 'OrderSaga'])]);

      await tick(0, 'Legacy', false);

      expect(host.grants()[0].sagaTypes).toEqual(['OrderSaga']);
      expect(typeLabels(0)).toEqual(['OrderSaga', 'PaymentSaga']);
    });
  });

  describe('a saga type typed in by its exact name', () => {
    it('adds it, trimmed, as a ticked checkbox, and empties the box and keeps the focus there for the next', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga'])]);

      await typeName(0, '  ShippingSaga  ');
      await addTyped(0);

      expect(host.grants()[0].sagaTypes).toEqual(['OrderSaga', 'ShippingSaga']);
      expect(typeLabels(0)).toEqual(['OrderSaga', 'PaymentSaga', 'ShippingSaga']);
      expect(ticked(0)).toEqual(['OrderSaga', 'ShippingSaga']);
      expect(customInput(0).value).toBe('');
      expect(focused()).toBe(customInput(0));
    });

    it('adds it on Enter, and does not submit the page that holds the form', async () => {
      await render([grant(OPERATOR_ID, [])]);
      await typeName(0, 'ShippingSaga');

      const enter = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true, bubbles: true });
      customInput(0).dispatchEvent(enter);
      await settle();

      expect(host.grants()[0].sagaTypes).toEqual(['ShippingSaga']);
      expect(enter.defaultPrevented).toBe(true);
      expect(host.submits).toBe(0);
    });

    it('keeps the case it was typed in: saga types are compared ordinally', async () => {
      await render([grant(OPERATOR_ID, [])]);

      await typeName(0, 'ordersaga');
      await addTyped(0);

      expect(host.grants()[0].sagaTypes).toEqual(['ordersaga']);
    });

    it.each([
      ['nothing', ''],
      ['blanks', '   '],
    ])('refuses %s, says so beside the box, and changes no grant', async (_what, value) => {
      await render([grant(OPERATOR_ID, ['OrderSaga'])]);

      await typeName(0, value);
      await addTyped(0);

      expect(host.grants()).toEqual([grant(OPERATOR_ID, ['OrderSaga'])]);
      const message = group(0).querySelector('.custom .field-error');
      expect(message?.textContent).toBe('Enter a saga type name.');
      expect(customInput(0).getAttribute('aria-invalid')).toBe('true');
      expect(customInput(0).getAttribute('aria-describedby')).toContain(message!.id);
    });

    it('refuses a name over the API limit and a name with a control character', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga'])]);

      await typeName(0, 'x'.repeat(201));
      await addTyped(0);
      expect(group(0).querySelector('.custom .field-error')?.textContent).toBe(
        'Use at most 200 characters.',
      );

      await typeName(0, 'Order\tSaga');
      await addTyped(0);
      expect(group(0).querySelector('.custom .field-error')?.textContent).toBe(
        'Use no control characters.',
      );
      expect(host.grants()[0].sagaTypes).toEqual(['OrderSaga']);
    });

    it('limits what can be typed to the API limit, by attribute', async () => {
      await render([grant(OPERATOR_ID, [])]);

      expect(customInput(0).getAttribute('maxlength')).toBe('200');
    });

    it('ends the message as soon as something is typed, and keeps what was typed', async () => {
      await render([grant(OPERATOR_ID, [])]);
      await typeName(0, ' ');
      await addTyped(0);
      expect(group(0).querySelector('.custom .field-error')).not.toBeNull();

      await typeName(0, 'S');

      expect(group(0).querySelector('.custom .field-error')).toBeNull();
      expect(customInput(0).getAttribute('aria-invalid')).toBeNull();
      expect(customInput(0).value).toBe('S');
    });

    it('does not name a saga type twice, whether it is ticked, or typed again', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga'])]);

      await typeName(0, 'OrderSaga');
      await addTyped(0);
      await typeName(0, 'ShippingSaga');
      await addTyped(0);
      await typeName(0, 'ShippingSaga');
      await addTyped(0);

      expect(host.grants()[0].sagaTypes).toEqual(['OrderSaga', 'ShippingSaga']);
    });

    it('adds a name that is a known saga type by ticking it', async () => {
      await render([grant(OPERATOR_ID, [])]);

      await typeName(0, 'PaymentSaga');
      await addTyped(0);

      expect(host.grants()[0].sagaTypes).toEqual(['PaymentSaga']);
      expect(typeLabels(0)).toEqual(['OrderSaga', 'PaymentSaga']);
    });

    it('keeps the name typed in one grant with that grant when another is removed before it', async () => {
      await render([grant(OPERATOR_ID, []), grant(SUPPORT.id, [])]);
      await typeName(1, 'Half');

      button('Remove', group(0)).click();
      await settle();

      expect(groups()).toHaveLength(1);
      expect(customInput(0).value).toBe('Half');
      expect(host.grants().map((g) => g.roleId)).toEqual([SUPPORT.id]);
    });
  });

  describe('a scoped grant that names no saga type', () => {
    it('says "Pick at least one saga type", and ties it to the group of checkboxes', async () => {
      await render([grant(OPERATOR_ID, [])]);

      const message = Array.from(group(0).querySelectorAll('.field-error')).find((m) =>
        m.textContent?.includes('Pick at least one saga type'),
      )!;
      expect(message).toBeDefined();
      expect(group(0).querySelector('fieldset.types')?.getAttribute('aria-describedby')).toBe(
        message.id,
      );
    });

    it('stops saying it once a saga type is picked, or typed, or the grant is for every saga type', async () => {
      await render([grant(OPERATOR_ID, []), grant(VIEWER_ID, []), grant(SUPPORT.id, [])]);
      expect(el().textContent?.match(/Pick at least one saga type/g)).toHaveLength(3);

      await tick(0, 'OrderSaga');
      await typeName(1, 'Typed');
      await addTyped(1);
      await choose(2, 'All saga types');

      expect(el().textContent).not.toContain('Pick at least one saga type');
    });

    it('is what scopedWithoutTypes finds, by position, for the page to block its Save on', async () => {
      expect(
        scopedWithoutTypes([
          grant(OPERATOR_ID, ['A']),
          grant(VIEWER_ID, []),
          grant(SUPPORT.id),
          { roleId: 'x', allSagaTypes: false, sagaTypes: [] },
        ]),
      ).toEqual([1, 3]);
      expect(scopedWithoutTypes([])).toEqual([]);
      expect(scopedWithoutTypes([grant(OPERATOR_ID), grant(VIEWER_ID, ['A'])])).toEqual([]);
    });
  });

  describe('access.manage in a scoped grant', () => {
    const hint = 'access.manage is ignored in a scoped grant';

    it('says it is ignored when the role holds it and the grant is for selected saga types', async () => {
      await render([grant(ADMINISTRATOR_ID, ['OrderSaga'])]);

      expect(text(0)).toContain(hint);
    });

    it('says nothing for a grant for every saga type, or for a role without it', async () => {
      await render([grant(ADMINISTRATOR_ID), grant(OPERATOR_ID, ['OrderSaga'])]);

      expect(text(0)).not.toContain(hint);
      expect(text(1)).not.toContain(hint);
    });

    it('follows the role and the scope as they change', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga'])]);
      expect(text(0)).not.toContain(hint);

      await pickRole(0, ADMINISTRATOR_ID);
      expect(text(0)).toContain(hint);

      await choose(0, 'All saga types');
      expect(text(0)).not.toContain(hint);
    });
  });

  describe('Remove', () => {
    it('takes the grant away and nothing else, and says which grant it is for', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga']), grant(VIEWER_ID)]);
      const remove = button('Remove', group(0));
      expect(remove.getAttribute('aria-label')).toBe('Remove the Operator grant');

      remove.click();
      await settle();

      expect(host.grants()).toEqual([grant(VIEWER_ID)]);
      expect(groups()).toHaveLength(1);
      // Where focus can go on: the row it was in is gone.
      expect(focused()).toBe(addGrant());
    });
  });

  describe('what the API said', () => {
    const grants = () => [
      grant(OPERATOR_ID, []),
      grant(VIEWER_ID, ['OrderSaga']),
      grant(SUPPORT.id),
    ];

    it("puts each message with its grant: the role's under the select, the saga types' under the types", async () => {
      await render(grants(), (h) =>
        h.errors.set({
          'grants[0].sagaTypes': ['Name 1 to 100 saga types, or grant all saga types.'],
          'grants[1].roleId': ['No role has this id.'],
        }),
      );

      expect(text(0)).toContain('Name 1 to 100 saga types, or grant all saga types.');
      expect(text(0)).not.toContain('No role has this id.');
      expect(text(1)).toContain('No role has this id.');
      expect(text(1)).not.toContain('Name 1 to 100');
      expect(text(2)).not.toContain('No role has this id.');
      expect(text(2)).not.toContain('Name 1 to 100');
    });

    it('marks the role select and describes it by its message', async () => {
      await render(grants(), (h) => h.errors.set({ 'grants[1].roleId': ['No role has this id.'] }));

      const message = group(1).querySelector('.role .field-error')!;
      expect(select(1).getAttribute('aria-invalid')).toBe('true');
      expect(select(1).getAttribute('aria-describedby')).toBe(message.id);
      expect(select(0).getAttribute('aria-invalid')).toBeNull();
    });

    it('describes the checkboxes by their message', async () => {
      await render(grants(), (h) =>
        h.errors.set({ 'grants[1].sagaTypes': ["'OrderSaga' is listed twice."] }),
      );

      const message = Array.from(group(1).querySelectorAll('.field-error')).find((m) =>
        m.textContent?.includes('listed twice'),
      )!;
      expect(group(1).querySelector('fieldset.types')?.getAttribute('aria-describedby')).toBe(
        message.id,
      );
    });

    it('shows a message about a grant as a whole at the top of that grant, and about the list above all', async () => {
      await render(grants(), (h) =>
        h.errors.set({
          grants: ['Hold at most 20 grants.'],
          'grants[2]': ['Each grant names a role and its saga types.'],
        }),
      );

      expect(
        el().querySelector(':scope > form > app-grants-editor > .field-error')?.textContent,
      ).toBe('Hold at most 20 grants.');
      expect(group(2).querySelector(':scope > .field-error')?.textContent).toBe(
        'Each grant names a role and its saga types.',
      );
    });

    it('shows a message about a grant that is not there any more with the list as a whole, so it is not lost', async () => {
      await render([grant(OPERATOR_ID, ['A'])], (h) =>
        h.errors.set({ 'grants[3].roleId': ['No role has this id.'] }),
      );

      expect(el().textContent).toContain('No role has this id.');
      expect(
        el().querySelector(':scope > form > app-grants-editor > .field-error')?.textContent,
      ).toBe('No role has this id.');
    });

    it('ignores what is not about grants', async () => {
      await render(grants(), (h) => h.errors.set({ displayName: ['Enter 1 to 128 characters.'] }));

      expect(el().textContent).not.toContain('Enter 1 to 128 characters.');
      expect(el().querySelector('.field-error')?.textContent).toContain('Pick at least one');
    });

    it('moves focus to the first grant with a problem: the role for a message about it, else the saga types', async () => {
      await render(grants(), (h) => h.errors.set({ 'grants[1].roleId': ['No role has this id.'] }));

      // The first grant names no saga type: that is the first problem, and its first checkbox the place for it.
      expect(host.editor().focusProblem()).toBe(true);
      await settle();
      expect(focused()).toBe(boxes(0)[0]);

      await tick(0, 'OrderSaga');
      expect(host.editor().focusProblem()).toBe(true);
      await settle();
      expect(focused()).toBe(select(1));
    });

    it('moves focus to the box for a name when no saga type is known to tick', async () => {
      await render([grant(OPERATOR_ID, [])], (h) => h.sagaTypes.set([]));

      expect(host.editor().focusProblem()).toBe(true);
      await settle();

      expect(focused()).toBe(customInput(0));
    });

    it('has no problem to move to when nothing is wrong, a grant for every saga type included', async () => {
      await render([grant(OPERATOR_ID, ['OrderSaga']), grant(VIEWER_ID)]);
      const before = focused();

      expect(host.editor().focusProblem()).toBe(false);
      await settle();

      expect(focused()).toBe(before);
    });
  });

  describe('grantsBody', () => {
    it('is the grants as a request carries them: a grant for every saga type names none, a name is trimmed', () => {
      const body = grantsBody([
        { roleId: 'a', allSagaTypes: true, sagaTypes: ['left over'] },
        { roleId: 'b', allSagaTypes: false, sagaTypes: [' X ', 'Y'] },
      ]);

      expect(body).toEqual([
        { roleId: 'a', allSagaTypes: true, sagaTypes: [] },
        { roleId: 'b', allSagaTypes: false, sagaTypes: ['X', 'Y'] },
      ]);
    });

    it('holds nothing but the three members the API reads, and does not change what it was given', () => {
      const given: Grant[] = [{ roleId: 'a', allSagaTypes: false, sagaTypes: [' X'] }];

      const [body] = grantsBody([{ ...given[0], extra: 1 } as Grant]);

      expect(Object.keys(body).sort()).toEqual(['allSagaTypes', 'roleId', 'sagaTypes']);
      expect(given[0].sagaTypes).toEqual([' X']);
    });
  });
});
