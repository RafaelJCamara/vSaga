import { Component, computed, input } from '@angular/core';
import { SagaStatus } from '../../models/saga.model';

const STATUS_CLASS: Record<SagaStatus, string> = {
  Running: 'running',
  Completed: 'completed',
  Failed: 'failed',
  Compensating: 'compensating',
  Compensated: 'compensated',
  TimedOut: 'timedout',
  Cancelled: 'cancelled',
};

@Component({
  selector: 'app-status-badge',
  imports: [],
  templateUrl: './status-badge.html',
  styleUrl: './status-badge.scss',
})
export class StatusBadge {
  readonly status = input.required<SagaStatus>();
  readonly cssClass = computed(() => STATUS_CLASS[this.status()]);
}
