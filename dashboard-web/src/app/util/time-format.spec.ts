import { formatLocal, formatOffset, formatRecordedAt, timezoneLabel } from './time-format';

// Every call passes an explicit zone: CI runs in UTC and developer machines do not.
describe('time-format', () => {
  describe('formatOffset', () => {
    it('writes seconds with milliseconds under a minute', () => {
      expect(formatOffset(0)).toBe('+0.000 s');
      expect(formatOffset(1204)).toBe('+1.204 s');
      expect(formatOffset(59_999)).toBe('+59.999 s');
    });

    it('writes minutes, seconds and milliseconds under an hour', () => {
      expect(formatOffset(60_000)).toBe('+1:00.000');
      expect(formatOffset(125_300)).toBe('+2:05.300');
    });

    it('drops milliseconds from an hour up', () => {
      expect(formatOffset(3_723_000)).toBe('+1:02:03');
      expect(formatOffset(3_723_999)).toBe('+1:02:03');
    });

    it('counts days past a day', () => {
      expect(formatOffset(2 * 86_400_000 + 3_723_000)).toBe('+2d 01:02:03');
      expect(formatOffset(86_400_000)).toBe('+1d 00:00:00');
    });

    it('keeps the same forms behind a minus sign for a negative offset', () => {
      expect(formatOffset(-1500)).toBe('-1.500 s');
      expect(formatOffset(-125_300)).toBe('-2:05.300');
    });

    it('rounds fractional milliseconds and rejects non-numbers', () => {
      expect(formatOffset(1204.4)).toBe('+1.204 s');
      expect(formatOffset(Number.NaN)).toBe('');
      expect(formatOffset(Number.POSITIVE_INFINITY)).toBe('');
    });
  });

  describe('formatRecordedAt', () => {
    const origin = '2026-03-14T12:03:05.916Z';

    it('writes the local time, the exact UTC text and the offset from the origin', () => {
      expect(formatRecordedAt('2026-03-14T12:03:07.140Z', origin, 'UTC')).toEqual({
        local: '12:03:07.140',
        utc: '2026-03-14 12:03:07.140 UTC',
        offset: '+1.224 s',
      });
    });

    it('writes the local time in an explicit +0200 zone while the UTC text stays UTC', () => {
      expect(formatRecordedAt('2026-03-14T12:03:07.140Z', origin, '+0200')).toEqual({
        local: '14:03:07.140',
        utc: '2026-03-14 12:03:07.140 UTC',
        offset: '+1.224 s',
      });
    });

    it('prefixes the date when the local day differs from the origin day', () => {
      const late = '2026-03-14T22:30:00.000Z';
      const start = '2026-03-14T21:00:00.000Z';

      // In +0200 the entry is past local midnight; the origin is not.
      expect(formatRecordedAt(late, start, '+0200').local).toBe('Mar 15, 00:30:00.000');
      expect(formatRecordedAt(late, start, '+0200').offset).toBe('+1:30:00');
      // In UTC both fall on the same day.
      expect(formatRecordedAt(late, start, 'UTC').local).toBe('22:30:00.000');
    });

    it('reads the seven fractional digits and offsets .NET writes', () => {
      expect(formatRecordedAt('2026-03-14T12:03:07.1401234Z', origin, 'UTC')).toEqual({
        local: '12:03:07.140',
        utc: '2026-03-14 12:03:07.140 UTC',
        offset: '+1.224 s',
      });
      expect(formatRecordedAt('2026-03-14T14:03:07.14+02:00', origin, 'UTC').utc).toBe(
        '2026-03-14 12:03:07.140 UTC',
      );
    });

    it('has no offset and no day prefix without a usable origin', () => {
      expect(formatRecordedAt('2026-03-15T12:03:07.140Z', null, 'UTC')).toEqual({
        local: '12:03:07.140',
        utc: '2026-03-15 12:03:07.140 UTC',
        offset: '',
      });
      expect(formatRecordedAt('2026-03-15T12:03:07.140Z', 'garbage', 'UTC').offset).toBe('');
    });

    it('returns the raw text for unparseable input', () => {
      expect(formatRecordedAt('not a date', origin, 'UTC')).toEqual({
        local: 'not a date',
        utc: 'not a date',
        offset: '',
      });
      expect(formatRecordedAt('', origin, 'UTC')).toEqual({ local: '', utc: '', offset: '' });
    });
  });

  describe('formatLocal', () => {
    it('writes a time or a date and time in the given zone', () => {
      expect(formatLocal('2026-03-14T12:03:07.140Z', 'time', '+0200')).toBe('14:03:07.140');
      expect(formatLocal('2026-03-14T23:03:07.140Z', 'datetime', '+0200')).toBe(
        'Mar 15, 2026, 01:03:07.140',
      );
      expect(formatLocal('2026-03-14T12:03:07.140Z', 'datetime', 'UTC')).toBe(
        'Mar 14, 2026, 12:03:07.140',
      );
    });

    it('returns the raw text for unparseable input', () => {
      expect(formatLocal('yesterday-ish', 'datetime', 'UTC')).toBe('yesterday-ish');
    });
  });

  describe('timezoneLabel', () => {
    const at = new Date('2026-03-14T12:00:00Z');

    it('names an offset zone', () => {
      expect(timezoneLabel(at, '+0200')).toBe('UTC+02:00');
      expect(timezoneLabel(at, '-0530')).toBe('UTC-05:30');
    });

    it('says plain UTC for a zero offset', () => {
      expect(timezoneLabel(at, 'UTC')).toBe('UTC');
      expect(timezoneLabel(at, '+0000')).toBe('UTC');
    });

    it('defaults to the viewer zone now', () => {
      expect(timezoneLabel()).toMatch(/^UTC([+-]\d\d:\d\d)?$/);
    });
  });
});
