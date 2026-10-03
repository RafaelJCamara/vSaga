import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { formatLocal, formatRecordedAt } from '../../util/time-format';

/**
 * One instant in the viewer's local time, with the exact UTC instant on hover (`title`) and the
 * machine-readable value in `datetime`. Unparseable input is shown as it came.
 */
@Component({
  selector: 'app-local-time',
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `<time [attr.datetime]="value()" [attr.title]="utc()">{{ local() }}</time>`,
})
export class LocalTime {
  /** An ISO 8601 instant, as the API sends it. */
  readonly value = input.required<string>();
  /** `time` is `HH:mm:ss.SSS`; `datetime` adds the date (`MMM d, y, `). */
  readonly format = input<'time' | 'datetime'>('time');

  readonly local = computed(() => formatLocal(this.value(), this.format()));
  readonly utc = computed(() => formatRecordedAt(this.value(), null).utc);
}
