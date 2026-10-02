/**
 * Reading a saga's persisted state (and message payloads) for display. Pure and Angular-free.
 *
 * The .NET engine serializes `Kind` and `Status` on the saga data as their underlying enum ints
 * ("Kind": 0, "Status": 2) rather than names. Everywhere else this UI shows the string form, so the
 * state readers remap those two top-level fields for display only; the stored JSON is untouched and
 * nested fields that happen to be called `Kind` or `Status` belong to the saga's own model.
 */

/** Index = C#'s SagaKind enum order. */
export const SAGA_KINDS = ['Orchestrated', 'Choreographed'] as const;

/** Index = C#'s SagaStatus enum order (mirrors STATUSES in saga-list.ts). */
export const SAGA_STATUSES = [
  'Running',
  'Completed',
  'Failed',
  'Compensating',
  'Compensated',
  'TimedOut',
  'Cancelled',
] as const;

/**
 * A stored JSON text, read:
 * - `empty`: nothing stored (or withheld; callers that care test the raw value for null first);
 * - `value`: valid JSON, with top-level numeric Kind/Status already read as names;
 * - `invalid`: not JSON, kept as its raw text;
 * - `omitted`: an omission marker written in place of a value too large to keep. `limit` is the
 *   per-value cap a marker reports, `budget` the per-saga snapshot budget a budget marker reports.
 */
export type ParsedJson =
  | { kind: 'empty' }
  | { kind: 'value'; value: unknown }
  | { kind: 'invalid'; raw: string }
  | { kind: 'omitted'; bytes: number | null; limit: number | null; budget: number | null };

/**
 * MongoDB's `$vsagaPayloadOmitted` (MongoSagaEventLogStore) and the engine's `$vsagaStateOmitted`
 * both match, and so will any later marker that follows the same naming.
 */
const OMISSION_KEY = /^\$vsaga\w*Omitted$/;

type Decoded = { ok: true; value: unknown } | { ok: false };

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function enumName(names: readonly string[], value: unknown): unknown {
  return typeof value === 'number' ? (names[value] ?? value) : value;
}

/** JSON.parse plus the display-only enum remap; never throws. */
function decode(json: string, mapEnums: boolean): Decoded {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return { ok: false };
  }
  if (mapEnums && isPlainObject(parsed)) {
    if ('Kind' in parsed) parsed['Kind'] = enumName(SAGA_KINDS, parsed['Kind']);
    if ('Status' in parsed) parsed['Status'] = enumName(SAGA_STATUSES, parsed['Status']);
  }
  return { ok: true, value: parsed };
}

function numberOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/**
 * An omission marker as read from JSON. The never-present brand keeps it a narrower type than
 * `Record<string, unknown>`, so a guard on a record leaves the record (not `never`) in its false
 * branch.
 */
export interface OmissionMarker {
  readonly [key: string]: unknown;
  readonly bytes?: unknown;
  readonly limit?: unknown;
  readonly budget?: unknown;
  readonly __vsagaOmissionMarker?: never;
}

/** Whether `value` is an omission marker: an object with an own `$vsaga…Omitted` key set to true. */
export function isOmissionMarker(value: unknown): value is OmissionMarker {
  return (
    isPlainObject(value) &&
    Object.keys(value).some((key) => OMISSION_KEY.test(key) && value[key] === true)
  );
}

/** Reads a saga state blob; top-level numeric Kind/Status become their names. */
export function parseStateJson(json: string | null | undefined): ParsedJson {
  if (!json) return { kind: 'empty' };
  const decoded = decode(json, true);
  if (!decoded.ok) return { kind: 'invalid', raw: json };
  const value = decoded.value;
  if (isOmissionMarker(value)) {
    return {
      kind: 'omitted',
      bytes: numberOrNull(value['bytes']),
      limit: numberOrNull(value['limit']),
      budget: numberOrNull(value['budget']),
    };
  }
  return { kind: 'value', value };
}

/**
 * A saga state blob pretty-printed with two-space indentation and Kind/Status as names; '' when
 * nothing is stored; the raw text unchanged when it is not JSON.
 */
export function formatStateJson(json: string | null | undefined): string {
  return format(json, true);
}

/** Like formatStateJson, without the enum remap: for message payloads, whose fields are their own. */
export function prettyJson(json: string | null | undefined): string {
  return format(json, false);
}

function format(json: string | null | undefined, mapEnums: boolean): string {
  if (!json) return '';
  const decoded = decode(json, mapEnums);
  return decoded.ok ? JSON.stringify(decoded.value, null, 2) : json;
}
