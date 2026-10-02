/**
 * A structural diff of two parsed JSON values, for "what changed in this step". Pure and
 * Angular-free.
 *
 * The walk is recursive and depth-first and stops once `max` changes are collected:
 * 1. Two plain objects: the keys of `after` in order, then the keys only in `before`. A key missing
 *    on one side is one `added` or `removed` change carrying the whole subtree.
 * 2. Two arrays: index by index, then one `added` or `removed` per surplus index. There is no move
 *    detection, so an insertion at the head of a list reads as a change at every index after it.
 * 3. Anything else (primitives, or two different JSON types): `changed` unless `Object.is`-equal.
 *
 * Paths read `Order.Lines[2].Quantity`; a key that is not an identifier prints as `["odd key"]`.
 * A change to the whole value (two different primitives at the top) has the empty path.
 */

export interface JsonChange {
  path: string;
  kind: 'added' | 'removed' | 'changed';
  before?: unknown;
  after?: unknown;
}

export interface JsonDiff {
  changes: JsonChange[];
  /** True when the walk stopped at `max` with differences left unreported. */
  truncated: boolean;
}

const IDENTIFIER = /^[A-Za-z_$][\w$]*$/;

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function keyPath(parent: string, key: string): string {
  if (!IDENTIFIER.test(key)) return `${parent}[${JSON.stringify(key)}]`;
  return parent === '' ? key : `${parent}.${key}`;
}

function indexPath(parent: string, index: number): string {
  return `${parent}[${index}]`;
}

class DiffWalk {
  readonly changes: JsonChange[] = [];
  truncated = false;

  constructor(private readonly max: number) {}

  /** Records one change; false (and `truncated`) once the cap is reached. */
  private push(change: JsonChange): boolean {
    if (this.changes.length >= this.max) {
      this.truncated = true;
      return false;
    }
    this.changes.push(change);
    return true;
  }

  /** Walks one pair of values; false once the walk must stop. */
  walk(before: unknown, after: unknown, path: string): boolean {
    if (isPlainObject(before) && isPlainObject(after)) return this.walkObjects(before, after, path);
    if (Array.isArray(before) && Array.isArray(after)) return this.walkArrays(before, after, path);
    if (Object.is(before, after)) return true;
    return this.push({ path, kind: 'changed', before, after });
  }

  private walkObjects(
    before: Record<string, unknown>,
    after: Record<string, unknown>,
    path: string,
  ): boolean {
    for (const key of Object.keys(after)) {
      const childPath = keyPath(path, key);
      const ok = Object.hasOwn(before, key)
        ? this.walk(before[key], after[key], childPath)
        : this.push({ path: childPath, kind: 'added', after: after[key] });
      if (!ok) return false;
    }
    for (const key of Object.keys(before)) {
      if (Object.hasOwn(after, key)) continue;
      if (!this.push({ path: keyPath(path, key), kind: 'removed', before: before[key] }))
        return false;
    }
    return true;
  }

  private walkArrays(before: readonly unknown[], after: readonly unknown[], path: string): boolean {
    const shared = Math.min(before.length, after.length);
    for (let i = 0; i < shared; i++) {
      if (!this.walk(before[i], after[i], indexPath(path, i))) return false;
    }
    for (let i = shared; i < after.length; i++) {
      if (!this.push({ path: indexPath(path, i), kind: 'added', after: after[i] })) return false;
    }
    for (let i = shared; i < before.length; i++) {
      if (!this.push({ path: indexPath(path, i), kind: 'removed', before: before[i] }))
        return false;
    }
    return true;
  }
}

/** The changes that turn `before` into `after`, at most `max` of them. */
export function diffJson(before: unknown, after: unknown, max = 200): JsonDiff {
  const walk = new DiffWalk(max);
  walk.walk(before, after, '');
  return { changes: walk.changes, truncated: walk.truncated };
}

/** A value as compact JSON, cut to `maxChars` characters with an ellipsis when longer. */
export function previewValue(value: unknown, maxChars = 120): string {
  const text = value === undefined ? 'undefined' : JSON.stringify(value);
  return text.length > maxChars ? `${text.slice(0, Math.max(0, maxChars - 1))}…` : text;
}
