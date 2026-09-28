import { TestBed } from '@angular/core/testing';
import { SagaStatus } from '../../models/saga.model';
import { StatusBadge } from './status-badge';

describe('StatusBadge', () => {
  const cases: Array<[SagaStatus, string]> = [
    ['Running', 'status-badge--running'],
    ['Completed', 'status-badge--completed'],
    ['Failed', 'status-badge--failed'],
    ['Compensating', 'status-badge--compensating'],
    ['Compensated', 'status-badge--compensated'],
    ['TimedOut', 'status-badge--timedout'],
    ['Cancelled', 'status-badge--cancelled'],
  ];

  for (const [status, cssClass] of cases) {
    it(`renders ${status} with the ${cssClass} class`, () => {
      const fixture = TestBed.createComponent(StatusBadge);
      fixture.componentRef.setInput('status', status);
      fixture.detectChanges();

      const el: HTMLElement = fixture.nativeElement;
      expect(el.textContent).toContain(status);
      expect(el.querySelector(`.${cssClass}`)).not.toBeNull();
    });
  }
});
