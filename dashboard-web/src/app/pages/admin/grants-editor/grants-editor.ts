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
import { Grant, Role } from '../admin.model';

/** The request member the grants are written under, on a user and on a team: the key of the API's errors. */
const FIELD = 'grants';

/** The API's limit on a saga type name (`AccessValidation.MaxSagaTypeLength`), so the form says so before sending. */
const MAX_SAGA_TYPE = 200;

/** Held for every saga type only: the API never scopes it. */
const ACCESS_MANAGE = 'access.manage';

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
  /** The role holds `access.manage` and the grant is scoped, so that permission does nothing here. */
  manageIgnored: boolean;
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
 * It says, beside the grant, what Save cannot send: "Pick at least one saga type", and that `access.manage` is
 * ignored in a scoped grant. The page blocks its Save with `scopedWithoutTypes` and builds the request with
 * `grantsBody`. `errors` are the API's messages by request path (`grants`, `grants[0]`, `grants[0].roleId`,
 * `grants[1].sagaTypes`): each goes with its grant, and `focusProblem()` moves focus to the first. The page
 * clears them when `grants` changes, since they were about the value that was sent.
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
  /** The API's messages about the grants, by request path; paths that are not below `grants` are ignored. */
  readonly errors = input<Readonly<Record<string, readonly string[]>>>({});

  protected readonly uid = `grants-${nextId++}`;
  /** What is wrong with the name typed in one grant's exact-name input (one at a time), by the grant's key. */
  private readonly typedProblem = signal<{ key: number; message: string } | null>(null);
  /** The grants' keys. A grant the editor changes hands its key to the new object; one that comes from outside
   *  (the page seeding another user, say) gets a new one, so what was typed beside the old one is gone. */
  private readonly keys = new WeakMap<Grant, number>();
  private nextKey = 0;

  protected readonly rows = computed<GrantRow[]>(() => {
    const grants = this.grants();
    const roles = this.roles();
    const known = this.sagaTypes();
    const errors = this.errors();
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
        manageIgnored: !grant.allSagaTypes && (role?.permissions.includes(ACCESS_MANAGE) ?? false),
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

  /** Moves focus to the first grant with something wrong (a message of the API, or no saga type picked). */
  focusProblem(): boolean {
    const index = this.rows().findIndex(
      (row) =>
        row.roleErrors.length > 0 ||
        row.typeErrors.length > 0 ||
        row.whole.length > 0 ||
        row.noTypes,
    );
    if (index < 0) return false;
    afterNextRender(
      () => {
        const row = this.rows()[index];
        // The role's own message goes to the role; the rest to where the saga types are chosen.
        const target = row?.roleErrors.length
          ? this.id('role', index)
          : this.firstTypeControl(index);
        this.host.querySelector<HTMLElement>(`#${target}`)?.focus();
      },
      { injector: this.injector },
    );
    return true;
  }

  protected id(part: string, index: number): string {
    return `${this.uid}-${part}-${index}`;
  }

  protected typeId(index: number, position: number): string {
    return `${this.uid}-type-${index}-${position}`;
  }

  protected add(): void {
    // Least privilege both ways: the role that holds the fewest permissions (the first of them in the select's
    // order when several do), not the first role, which is the administrator's; and no saga type until it is
    // told which, so that Save waits for the choice.
    const role = [...this.available()].sort(
      (a, b) => a.permissions.length - b.permissions.length,
    )[0];
    if (role === undefined) return;
    const index = this.grants().length;
    this.grants.set([...this.grants(), { roleId: role.id, allSagaTypes: false, sagaTypes: [] }]);
    afterNextRender(() => this.focusId(this.id('role', index)), { injector: this.injector });
  }

  protected remove(index: number): void {
    this.grants.set(this.grants().filter((_, i) => i !== index));
    this.typedProblem.set(null);
    afterNextRender(() => this.focusId(`${this.uid}-add`), { injector: this.injector });
  }

  protected setRole(index: number, event: Event): void {
    const roleId = (event.target as HTMLSelectElement).value;
    this.change(index, { roleId });
  }

  protected setAll(index: number, allSagaTypes: boolean): void {
    // A grant for every saga type names none: the API refuses one that does.
    this.change(index, { allSagaTypes, sagaTypes: [] });
  }

  protected toggle(index: number, name: string, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    const held = this.grants()[index].sagaTypes;
    this.change(index, {
      sagaTypes: on
        ? held.includes(name)
          ? held
          : [...held, name]
        : held.filter((n) => n !== name),
    });
  }

  /** What is typed beside a grant changed: what was said about the name before no longer applies. */
  protected typing(key: number): void {
    if (this.typedProblem()?.key === key) this.typedProblem.set(null);
  }

  /** Adds the saga type typed in `input`, trimmed. A blank or unusable name is said so, beside the input, and kept. */
  protected addTyped(index: number, input: HTMLInputElement): void {
    const name = input.value.trim();
    const problem = problemWith(name);
    const key = this.keyOf(this.grants()[index]);
    if (problem !== null) {
      this.typedProblem.set({ key, message: problem });
      return;
    }
    const held = this.grants()[index].sagaTypes;
    if (!held.includes(name)) this.change(index, { sagaTypes: [...held, name] });
    this.typedProblem.set(null);
    input.value = '';
    input.focus();
  }

  protected typedMessage(key: number): string | null {
    const problem = this.typedProblem();
    return problem?.key === key ? problem.message : null;
  }

  private change(index: number, changes: Partial<Grant>): void {
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
