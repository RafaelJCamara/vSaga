import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  model,
  signal,
} from '@angular/core';
import { belongsToField } from '../../../util/admin-failure';
import { Grant, PermissionInfo, Role } from '../admin.model';

/** The request member the grants are written under, on a user and on a team: the key of the API's errors. */
const FIELD = 'grants';

/** The API's limit on a saga type name (`AccessValidation.MaxSagaTypeLength`), so the form says so before sending. */
const MAX_SAGA_TYPE = 200;

/**
 * The positions of the grants that name saga types but none: such a grant confers nothing, the API refuses it
 * (`Name 1 to 100 saga types, or grant all saga types.`), and a page that saves grants blocks its Save on
 * them. Pure.
 */
export function scopedWithoutTypes(grants: readonly Grant[]): number[] {
  return grants.flatMap((grant, index) =>
    !grant.allSagaTypes && grant.sagaTypes.length === 0 ? [index] : [],
  );
}

/**
 * The grants as a request carries them: a grant for every saga type names none, and a name has no
 * surrounding blanks. Pure; the pages build their `grants` member with it.
 */
export function grantsBody(grants: readonly Grant[]): Grant[] {
  return grants.map((grant) => ({
    roleId: grant.roleId,
    allSagaTypes: grant.allSagaTypes,
    sagaTypes: grant.allSagaTypes ? [] : grant.sagaTypes.map((name) => name.trim()),
  }));
}

/** The first problem with `name` as a saga type, or null. */
function problemWith(name: string): string | null {
  if (name === '') return 'Enter a saga type name.';
  if (name.length > MAX_SAGA_TYPE) return `Use at most ${MAX_SAGA_TYPE} characters.`;
  if (/[\u0000-\u001f\u007f-\u009f]/.test(name)) return 'Use no control characters.';
  return null;
}

/** What the editor knows of one grant. */
interface GrantRow {
  grant: Grant;
  /** Names the row for as long as the grant lives, whatever its position or role: the DOM follows it. */
  key: number;
  /** The role's name; the editor says "Unknown role" for an id that no role has any more. */
  roleName: string;
  /** The roles this grant may pick: its own and those no other grant holds (one grant per role). */
  roles: readonly Role[];
  roleKnown: boolean;
  /** The checkboxes: the known saga types, then the granted ones that are not known, each once. */
  types: string[];
  /** Names saga types, none yet: Save is blocked. */
  noTypes: boolean;
  /** The permissions the role holds that the catalogue does not scope (`access.manage`), when the grant is scoped:
   *  they count only for every saga type, so they do nothing here. */
  ignored: string[];
  /** What the API said about this grant (a message about the grant as a whole, its role, its saga types). */
  whole: readonly string[];
  roleErrors: readonly string[];
  typeErrors: readonly string[];
}

let nextId = 0;

/**
 * The editor of a list of grants, the same on a user and on a team: it knows nothing of either. Each grant is
 * one role held for every saga type or for the ones selected; a role is held once (the API refuses two grants
 * of one role), so the select offers only roles no other grant uses and "Add grant" is off when none is left.
 * A scoped grant lists a checkbox per saga type the API knows (`sagaTypes`, which holds only types that have
 * instances) and per type the grant already names, and takes a type typed in by its exact name, trimmed
 * (Enter adds it, and does not submit the page's form), because a type that has not run yet is not listed.
 *
 * It says, beside the grant, what Save cannot send: "Pick at least one saga type", and that a permission the
 * `catalogue` does not scope (`access.manage`) is ignored in a scoped grant. The page blocks its Save with
 * `scopedWithoutTypes` and builds the request with `grantsBody`. `errors` are the API's messages by request path
 * (`grants`, `grants[0]`, `grants[0].roleId`, `grants[1].sagaTypes`): each goes with its grant, and
 * `focusProblem()` moves focus to the first (call it once the errors have been rendered into the editor: it reads
 * what the editor shows now). The page clears them when `grants` changes, since they were about the value that
 * was sent.
 *
 * Switching a grant to every saga type keeps the saga types it had ticked, so that switching back is no loss;
 * `grantsBody` leaves them out of the request. A name typed in the exact-name box and not yet added is added by
 * `commitTyped()`, which a page calls when it saves, so that Save never drops it silently. What the editor
 * did that moves or removes something the user was looking at ("Removed the Operator grant.") is said in an
 * always-present live region.
 */
@Component({
  selector: 'app-grants-editor',
  templateUrl: './grants-editor.html',
  styleUrl: './grants-editor.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GrantsEditor {
  private readonly host: HTMLElement = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly injector = inject(Injector);

  /** The grants being edited; every change writes a new array. */
  readonly grants = model.required<Grant[]>();
  /** The roles a grant may name, in the order the select lists them. */
  readonly roles = input.required<readonly Role[]>();
  /** The saga types the API knows. */
  readonly sagaTypes = input<readonly string[]>([]);
  /** The permission catalogue (`GET /api/admin/permissions`): which permissions a scoped grant cannot confer. */
  readonly catalogue = input<readonly PermissionInfo[]>([]);
  /** The API's messages about the grants, by request path; paths that are not below `grants` are ignored. */
  readonly errors = input<Readonly<Record<string, readonly string[]>>>({});

  protected readonly uid = `grants-${nextId++}`;
  /** What is wrong with the name typed in one grant's exact-name input (one at a time), by the grant's key. */
  private readonly typedProblem = signal<{ key: number; message: string } | null>(null);
  /** What the editor says in its live region: a removal, a name that was already there. */
  protected readonly announcement = signal('');
  /** The keys of the permissions the catalogue does not scope. */
  private readonly unscoped = computed(
    () =>
      new Set(
        this.catalogue()
          .filter((p) => !p.scopable)
          .map((p) => p.key),
      ),
  );
  /** The grants' keys. A grant the editor changes hands its key to the new object; one that comes from outside
   *  (the page seeding another user, say) gets a new one, so what was typed beside the old one is gone. */
  private readonly keys = new WeakMap<Grant, number>();
  private nextKey = 0;

  protected readonly rows = computed<GrantRow[]>(() => {
    const grants = this.grants();
    const roles = this.roles();
    const known = this.sagaTypes();
    const errors = this.errors();
    const unscoped = this.unscoped();
    const used = new Set(grants.map((g) => g.roleId));
    const messages = (path: string) => errors[path] ?? [];
    return grants.map((grant, index) => {
      const role = roles.find((r) => r.id === grant.roleId);
      const own = `${FIELD}[${index}]`;
      return {
        grant,
        key: this.keyOf(grant),
        roleName: role?.name ?? 'Unknown role',
        roles: roles.filter((r) => r.id === grant.roleId || !used.has(r.id)),
        roleKnown: role !== undefined,
        types: [...known, ...grant.sagaTypes.filter((name) => !known.includes(name))],
        noTypes: !grant.allSagaTypes && grant.sagaTypes.length === 0,
        ignored: grant.allSagaTypes
          ? []
          : (role?.permissions.filter((key) => unscoped.has(key)) ?? []),
        whole: messages(own),
        roleErrors: messages(`${own}.roleId`),
        typeErrors: messages(`${own}.sagaTypes`),
      };
    });
  });

  /** The messages about the list as a whole (`grants`), and about a grant at a position that no longer exists. */
  protected readonly general = computed(() => {
    const count = this.grants().length;
    return Object.entries(this.errors()).flatMap(([path, messages]) => {
      if (!belongsToField(path, FIELD)) return [];
      const match = /^grants\[(\d+)\]/.exec(path);
      return match === null || Number(match[1]) >= count ? messages : [];
    });
  });

  /** The roles no grant holds yet: what "Add grant" can offer. */
  protected readonly available = computed(() => {
    const used = new Set(this.grants().map((g) => g.roleId));
    return this.roles().filter((r) => !used.has(r.id));
  });

  /**
   * Moves focus to the first grant with something wrong (a message of the API, or no saga type picked); false when
   * there is none. It focuses at once, from what the editor shows now: a page that has just set the errors calls it
   * after the next render (`afterNextRender`), when they have reached the editor.
   */
  focusProblem(): boolean {
    const rows = this.rows();
    const index = rows.findIndex(
      (row) =>
        row.roleErrors.length > 0 ||
        row.typeErrors.length > 0 ||
        row.whole.length > 0 ||
        row.noTypes,
    );
    if (index < 0) return false;
    // The role's own message goes to the role; the rest to where the saga types are chosen.
    this.focusId(
      rows[index].roleErrors.length ? this.id('role', index) : this.firstTypeControl(index),
    );
    return true;
  }

  /**
   * Adds the names typed in the exact-name boxes that were not added with "Add type": a page calls it when it
   * saves, since a name left in a box would otherwise be dropped without a word. False when a name cannot be
   * added (its message is shown beside the box and the box has the focus): the page does not save then.
   */
  commitTyped(): boolean {
    for (let index = 0; index < this.grants().length; index++) {
      const input = this.host.querySelector<HTMLInputElement>(`#${this.id('custom', index)}`);
      if (input === null || input.value.trim() === '') continue;
      if (!this.addTyped(index, input)) return false;
    }
    return true;
  }

  protected id(part: string, index: number): string {
    return `${this.uid}-${part}-${index}`;
  }

  protected typeId(index: number, position: number): string {
    return `${this.uid}-type-${index}-${position}`;
  }

  protected add(): void {
    // Least privilege both ways: a role that holds nothing the catalogue keeps for every saga type (access.manage)
    // before one that does, then the one with the fewest permissions (the first of them in the select's order when
    // several do), not the first role, which is the administrator's; and no saga type until it is told which, so
    // that Save waits for the choice.
    const unscoped = this.unscoped();
    const rank = (role: Role) =>
      (role.permissions.some((key) => unscoped.has(key)) ? 1000 : 0) + role.permissions.length;
    const role = [...this.available()].sort((a, b) => rank(a) - rank(b))[0];
    if (role === undefined) return;
    this.announcement.set('');
    const index = this.grants().length;
    this.grants.set([...this.grants(), { roleId: role.id, allSagaTypes: false, sagaTypes: [] }]);
    afterNextRender(() => this.focusId(this.id('role', index)), { injector: this.injector });
  }

  protected remove(index: number): void {
    const name = this.rows()[index].roleName;
    this.grants.set(this.grants().filter((_, i) => i !== index));
    this.typedProblem.set(null);
    // The grant the user was looking at is gone, and the focus moves: say so.
    this.announcement.set(`Removed the ${name} grant.`);
    afterNextRender(() => this.focusId(`${this.uid}-add`), { injector: this.injector });
  }

  protected setRole(index: number, event: Event): void {
    const roleId = (event.target as HTMLSelectElement).value;
    this.change(index, { roleId });
  }

  protected setAll(index: number, allSagaTypes: boolean): void {
    // The saga types stay in the draft: an accidental click on "All saga types" loses no choice. `grantsBody`
    // leaves them out of the request (the API refuses a grant for every saga type that names some).
    this.change(index, { allSagaTypes });
  }

  protected toggle(index: number, name: string, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    const held = this.grants()[index].sagaTypes;
    // A type the API does not know is listed only while the grant names it: unticking it removes its checkbox,
    // and the focus it held goes to the next one (or the one before, or the exact-name box).
    const types = this.rows()[index].types;
    const vanishes = !on && !this.sagaTypes().includes(name);
    this.change(index, {
      sagaTypes: on
        ? held.includes(name)
          ? held
          : [...held, name]
        : held.filter((n) => n !== name),
    });
    if (!vanishes) return;
    const position = types.indexOf(name);
    const left = types.length - 1;
    this.announcement.set(`Removed ${name} from the list.`);
    const target =
      left === 0
        ? this.id('custom', index)
        : this.typeId(index, position < left ? position : left - 1);
    afterNextRender(() => this.focusId(target), { injector: this.injector });
  }

  /** What is typed beside a grant changed: what was said about the name before no longer applies. */
  protected typing(key: number): void {
    if (this.typedProblem()?.key === key) this.typedProblem.set(null);
  }

  /** Adds the saga type typed in `input`, trimmed. A blank or unusable name is said so, beside the input, and kept. */
  protected addTyped(index: number, input: HTMLInputElement): boolean {
    const name = input.value.trim();
    const problem = problemWith(name);
    const key = this.keyOf(this.grants()[index]);
    if (problem !== null) {
      this.typedProblem.set({ key, message: problem });
      input.focus();
      return false;
    }
    const held = this.grants()[index].sagaTypes;
    if (held.includes(name)) this.announcement.set(`${name} is already in the list.`);
    else this.change(index, { sagaTypes: [...held, name] });
    this.typedProblem.set(null);
    input.value = '';
    input.focus();
    return true;
  }

  protected typedMessage(key: number): string | null {
    const problem = this.typedProblem();
    return problem?.key === key ? problem.message : null;
  }

  private change(index: number, changes: Partial<Grant>): void {
    this.announcement.set('');
    this.grants.set(
      this.grants().map((grant, i) => {
        if (i !== index) return grant;
        const changed = { ...grant, ...changes };
        this.keys.set(changed, this.keyOf(grant));
        return changed;
      }),
    );
  }

  private keyOf(grant: Grant): number {
    let key = this.keys.get(grant);
    if (key === undefined) {
      key = this.nextKey++;
      this.keys.set(grant, key);
    }
    return key;
  }

  /** The control a grant's saga-type message is about: its first checkbox, or the exact-name input without any. */
  private firstTypeControl(index: number): string {
    return this.rows()[index].types.length > 0 ? this.typeId(index, 0) : this.id('custom', index);
  }

  private focusId(id: string): void {
    this.host.querySelector<HTMLElement>(`#${id}`)?.focus();
  }
}
