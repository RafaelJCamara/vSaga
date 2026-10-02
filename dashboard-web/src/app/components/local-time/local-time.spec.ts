import { TestBed } from '@angular/core/testing';
import { formatLocal } from '../../util/time-format';
import { LocalTime } from './local-time';

// The local text depends on the zone of the machine running the spec (CI runs in UTC, developer
// machines do not), so it is compared with the formatter's own output; the attributes are exact.
describe('LocalTime', () => {
  function render(value: string, format?: 'time' | 'datetime'): HTMLTimeElement {
    const fixture = TestBed.createComponent(LocalTime);
    fixture.componentRef.setInput('value', value);
    if (format) fixture.componentRef.setInput('format', format);
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('time');
  }

  it('renders a time element with the instant in datetime and the UTC time in its title', () => {
    const time = render('2026-03-14T12:03:05.916Z');

    expect(time.getAttribute('datetime')).toBe('2026-03-14T12:03:05.916Z');
    expect(time.getAttribute('title')).toBe('2026-03-14 12:03:05.916 UTC');
  });

  it('shows the local time of day by default', () => {
    const time = render('2026-03-14T12:03:05.916Z');

    expect(time.textContent).toBe(formatLocal('2026-03-14T12:03:05.916Z', 'time'));
    expect(time.textContent).toMatch(/^\d{2}:\d{2}:\d{2}\.\d{3}$/);
  });

  it('adds the date for the datetime format', () => {
    const time = render('2026-03-14T12:03:05.916Z', 'datetime');

    expect(time.textContent).toBe(formatLocal('2026-03-14T12:03:05.916Z', 'datetime'));
    expect(time.textContent).toMatch(/^Mar 1[45], 2026, /);
  });

  it('reads .NET timestamps with seven fractional digits', () => {
    const time = render('2026-03-14T12:03:05.9161234Z');

    expect(time.getAttribute('title')).toBe('2026-03-14 12:03:05.916 UTC');
  });

  it('shows unparseable input as it came', () => {
    const time = render('not a date');

    expect(time.textContent).toBe('not a date');
    expect(time.getAttribute('title')).toBe('not a date');
  });
});
