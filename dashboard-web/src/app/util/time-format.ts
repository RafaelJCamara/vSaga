import { formatDate } from '@angular/common';

/**
 * How the detail page writes times: the viewer's local clock, the exact UTC instant on hover, and
 * the offset from the saga's first entry. Pure apart from formatDate.
 *
 * Every function that renders a clock time takes an optional `timezone` in Angular's formatDate form
 * ('+0200', '-0530', 'UTC'); left out, it is the viewer's zone. formatOffset needs none. Specs always pass one, so they never depend on the zone
 * of the machine running them.
 */

export interface RecordedAt {
  /** `HH:mm:ss.SSS`, prefixed with `MMM d, ` when the local day differs from the origin's. */
  local: string;
  /** `yyyy-MM-dd HH:mm:ss.SSS UTC`. */
  utc: string;
  /** Offset from the origin (see formatOffset); '' without a usable origin. */
  offset: string;
}

/**
 * en-US is the locale built into Angular, and the app registers no other (no LOCALE_ID, no
 * registerLocaleData).
 */
const LOCALE = 'en-US';

const TIME = 'HH:mm:ss.SSS';
const DAY_PREFIX = 'MMM d, ';
const DATETIME = 'MMM d, y, HH:mm:ss.SSS';
const DAY_KEY = 'yyyy-MM-dd';

const SECOND = 1000;
const MINUTE = 60 * SECOND;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/**
 * Milliseconds since the epoch, or null when the text is not a date. .NET writes up to seven
 * fractional digits; Date.parse is only required to read three, so the rest are dropped first.
 */
function toMillis(iso: string | null | undefined): number | null {
  if (!iso) return null;
  const millis = Date.parse(iso.replace(/(\.\d{3})\d+/, '$1'));
  return Number.isNaN(millis) ? null : millis;
}

function pad(value: number, width = 2): string {
  return String(value).padStart(width, '0');
}

/** The labelled time of one entry, measured from `originIso` (the saga's first entry). */
export function formatRecordedAt(
  iso: string,
  originIso: string | null,
  timezone?: string,
): RecordedAt {
  const at = toMillis(iso);
  if (at === null) return { local: iso, utc: iso, offset: '' };

  const origin = toMillis(originIso);
  let local = formatDate(at, TIME, LOCALE, timezone);
  if (
    origin !== null &&
    formatDate(at, DAY_KEY, LOCALE, timezone) !== formatDate(origin, DAY_KEY, LOCALE, timezone)
  ) {
    local = formatDate(at, DAY_PREFIX, LOCALE, timezone) + local;
  }

  return {
    local,
    utc: `${formatDate(at, 'yyyy-MM-dd HH:mm:ss.SSS', LOCALE, 'UTC')} UTC`,
    offset: origin === null ? '' : formatOffset(at - origin),
  };
}

/** A time (`HH:mm:ss.SSS`) or date and time (`MMM d, y, HH:mm:ss.SSS`); the raw text when unparseable. */
export function formatLocal(iso: string, format: 'time' | 'datetime', timezone?: string): string {
  const at = toMillis(iso);
  if (at === null) return iso;
  return formatDate(at, format === 'time' ? TIME : DATETIME, LOCALE, timezone);
}

/**
 * An elapsed time, shortest form first: `+0.000 s`, `+1.204 s`, `+2:05.300`, `+1:02:03`,
 * `+2d 01:02:03`. Milliseconds are dropped from an hour up. A negative offset (clocks of two
 * processes disagreeing) keeps the same forms behind a minus sign.
 */
export function formatOffset(ms: number): string {
  if (!Number.isFinite(ms)) return '';
  const sign = ms < 0 ? '-' : '+';
  const total = Math.round(Math.abs(ms));

  if (total < MINUTE) return `${sign}${(total / SECOND).toFixed(3)} s`;

  const days = Math.floor(total / DAY);
  const hours = Math.floor((total % DAY) / HOUR);
  const minutes = Math.floor((total % HOUR) / MINUTE);
  const seconds = Math.floor((total % MINUTE) / SECOND);
  const millis = total % SECOND;

  if (total < HOUR) return `${sign}${minutes}:${pad(seconds)}.${pad(millis, 3)}`;
  if (total < DAY) return `${sign}${hours}:${pad(minutes)}:${pad(seconds)}`;
  return `${sign}${days}d ${pad(hours)}:${pad(minutes)}:${pad(seconds)}`;
}

/** The zone the local times are in, for one hint per list: `UTC+02:00`, `UTC-05:30`, `UTC`. */
export function timezoneLabel(at: Date = new Date(), timezone?: string): string {
  const offset = formatDate(at, 'ZZZZZ', LOCALE, timezone);
  return offset === 'Z' ? 'UTC' : `UTC${offset}`;
}
