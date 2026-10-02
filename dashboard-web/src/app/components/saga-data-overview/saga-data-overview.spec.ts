import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SagaLogEntry, SagaStatus, SagaSummary } from '../../models/saga.model';
import { numbered, persisted, received, started, succeeded } from '../../testing/timeline-fixtures';
import { foldTimeline } from '../../util/saga-transitions';
import { DataView, SagaDataOverview } from './saga-data-overview';

function summary(status: SagaStatus = 'Completed'): SagaSummary {
  return {
    correlationId: 'saga-1',
    sagaType: 'OrderSaga',
    kind: 'Orchestrated',
    currentState: 'Paid',
    status,
    createdAtUtc: '2026-01-01T00:00:00Z',
    updatedAtUtc: '2026-01-01T00:00:01Z',
    version: 3,
    parentSagaType: null,
    parentCorrelationId: null,
  };
}

/** Started by OrderSubmitted (step 1, snapshot Total 10), then PaymentCaptured (step 2, Total 25). */
function twoSteps(): SagaLogEntry[] {
  return numbered([
    started('m0', 'Submitted', { payloadJson: '{"OrderId":"o-1","Amount":10}' }),
    received('m0', 'OrderSubmitted'),
    succeeded('m0', 'Initial', 'Submitted'),
    persisted('m0', '{"Total":10,"Status":0,"Version":1}'),
    received('m1', 'PaymentCaptured'),
    succeeded('m1', 'Submitted', 'Paid'),
    persisted('m1', '{"Total":25,"Status":1,"Version":2}'),
  ]);
}

/** The same saga as recorded before snapshots existed. */
function noSnapshots(): SagaLogEntry[] {
  return twoSteps().filter((entry) => entry.entryType !== 'StatePersisted');
}

const CURRENT = '{"Total":25,"Status":1,"Version":3}';

interface Inputs {
  entries?: SagaLogEntry[];
  currentJson?: string | null;
  status?: SagaStatus;
  canViewData?: boolean;
  view?: DataView | null;
  historyLoaded?: boolean;
}

describe('SagaDataOverview', () => {
  function render(inputs: Inputs = {}): ComponentFixture<SagaDataOverview> {
    const fixture = TestBed.createComponent(SagaDataOverview);
    fixture.componentRef.setInput('history', foldTimeline(inputs.entries ?? twoSteps()));
    fixture.componentRef.setInput('currentJson', inputs.currentJson === undefined ? CURRENT : inputs.currentJson);
    fixture.componentRef.setInput('summary', summary(inputs.status));
    if (inputs.canViewData !== undefined) fixture.componentRef.setInput('canViewData', inputs.canViewData);
    if (inputs.view !== undefined) fixture.componentRef.setInput('view', inputs.view);
    if (inputs.historyLoaded !== undefined) fixture.componentRef.setInput('historyLoaded', inputs.historyLoaded);
    fixture.detectChanges();
    return fixture;
  }

  const text = (el: Element | null | undefined) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  function buttons(fixture: ComponentFixture<SagaDataOverview>): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.ov-bar button') as NodeListOf<HTMLButtonElement>);
  }

  function button(fixture: ComponentFixture<SagaDataOverview>, label: string): HTMLButtonElement | undefined {
    return buttons(fixture).find((b) => text(b) === label);
  }

  const panel = (fixture: ComponentFixture<SagaDataOverview>): HTMLElement | null =>
    fixture.nativeElement.querySelector('.ov-panel');

  const blocks = (fixture: ComponentFixture<SagaDataOverview>): string[] =>
    Array.from(panel(fixture)?.querySelectorAll('pre') ?? []).map((pre) => pre.textContent ?? '');

  function emitted(fixture: ComponentFixture<SagaDataOverview>): (DataView | null)[] {
    const seen: (DataView | null)[] = [];
    fixture.componentInstance.viewChange.subscribe((view) => seen.push(view));
    return seen;
  }

  it('is a "Saga data" group of three buttons with nothing open by default', () => {
    const fixture = render();

    const group = fixture.nativeElement.querySelector('[role="group"]');
    expect(group.getAttribute('aria-label')).toBe('Saga data');
    expect(text(group.querySelector('.micro-label'))).toBe('Saga data');
    expect(buttons(fixture).map((b) => text(b))).toEqual(['At start', 'At end', 'Compare']);
    expect(buttons(fixture).map((b) => b.getAttribute('aria-label'))).toEqual(['Data at start', 'Data at end', null]);
    expect(buttons(fixture).every((b) => !b.disabled && b.getAttribute('aria-pressed') === 'false')).toBe(true);
    expect(panel(fixture)).toBeNull();
  });

  it('asks for a view on click, and for none when the open one is clicked again', () => {
    const fixture = render({ view: 'end' });
    const seen = emitted(fixture);

    button(fixture, 'At start')!.click();
    button(fixture, 'Compare')!.click();
    button(fixture, 'At end')!.click();

    expect(seen).toEqual(['start', 'compare', null]);
  });

  describe('At start', () => {
    it('shows the message that started the saga and the state after the first recorded step', () => {
      const fixture = render({ view: 'start' });

      expect(button(fixture, 'At start')!.getAttribute('aria-pressed')).toBe('true');
      const captions = Array.from(panel(fixture)!.querySelectorAll('.ov-caption')).map(text);
      expect(captions).toEqual(['Message that started the saga OrderSubmitted', 'State after step 1 Started by OrderSubmitted']);
      expect(blocks(fixture)).toEqual([
        JSON.stringify({ OrderId: 'o-1', Amount: 10 }, null, 2),
        JSON.stringify({ Total: 10, Status: 'Running', Version: 1 }, null, 2),
      ]);
    });

    it('names a later step when the first steps recorded no snapshot', () => {
      const entries = twoSteps().filter((entry) => !(entry.entryType === 'StatePersisted' && entry.messageId === 'm0'));
      const fixture = render({ entries, view: 'start' });

      expect(text(panel(fixture)!.querySelectorAll('.ov-caption')[1])).toBe('State after step 2 PaymentCaptured');
      expect(blocks(fixture)[1]).toBe(JSON.stringify({ Total: 25, Status: 'Completed', Version: 2 }, null, 2));
    });

    it('says what is missing for a saga recorded before snapshots and payloads', () => {
      const entries = noSnapshots().map((entry) => ({ ...entry, payloadJson: null }));
      const fixture = render({ entries, view: 'start' });

      expect(blocks(fixture)).toEqual([]);
      expect(Array.from(panel(fixture)!.querySelectorAll('.ov-note')).map(text)).toEqual([
        'The message that started this saga was not recorded.',
        'No state snapshot was recorded for this saga: it ran before snapshots existed, or they are switched off.',
      ]);
    });
  });

  describe('At end and Current', () => {
    it.each<[SagaStatus, string, string, string]>([
      ['Completed', 'At end', 'Data at end', 'State at end'],
      ['Failed', 'At end', 'Data at end', 'State at end'],
      ['Compensated', 'At end', 'Data at end', 'State at end'],
      ['TimedOut', 'At end', 'Data at end', 'State at end'],
      ['Cancelled', 'At end', 'Data at end', 'State at end'],
      ['Running', 'Current', 'Current data', 'Current state'],
      ['Compensating', 'Current', 'Current data', 'Current state'],
    ])('a %s saga reads "%s"', (status, label, ariaLabel, caption) => {
      const fixture = render({ status, view: 'end' });

      const end = buttons(fixture)[1];
      expect(text(end)).toBe(label);
      expect(end.getAttribute('aria-label')).toBe(ariaLabel);
      expect(end.getAttribute('aria-pressed')).toBe('true');
      expect(text(panel(fixture)!.querySelector('.ov-caption'))).toBe(`${caption} Paid · version 3`);
    });

    it('shows the stored state, not a snapshot, with Kind and Status as names', () => {
      const fixture = render({ view: 'end' });

      expect(blocks(fixture)).toEqual([JSON.stringify({ Total: 25, Status: 'Completed', Version: 3 }, null, 2)]);
    });

    it('shows the stored state of a saga recorded before snapshots existed', () => {
      const fixture = render({ entries: noSnapshots(), view: 'end' });

      expect(blocks(fixture)).toEqual([JSON.stringify({ Total: 25, Status: 'Completed', Version: 3 }, null, 2)]);
    });

    it('says so when nothing is stored', () => {
      const fixture = render({ currentJson: null, view: 'end' });

      expect(blocks(fixture)).toEqual([]);
      expect(text(panel(fixture)!.querySelector('.ov-note'))).toBe('No state data recorded.');
    });
  });

  describe('Compare', () => {
    it('lists what changed between the first recorded snapshot and the stored state', () => {
      const fixture = render({ view: 'compare' });

      expect(text(panel(fixture)!.querySelector('.ov-caption'))).toBe(
        'Compare the state after step 1 (Started by OrderSubmitted) with the state at end',
      );
      const rows = Array.from(panel(fixture)!.querySelectorAll('.insp-diff tbody tr')).map(text);
      expect(rows).toEqual(['~Changed: Total 10 25', '~Changed: Status "Running" "Completed"']);
      expect(text(panel(fixture)!.querySelector('.insp-bookkeeping'))).toBe('Engine bookkeeping also changed: Version 1 → 3');
    });

    it('says nothing changed since then when the saga fields are the same', () => {
      const fixture = render({ currentJson: '{"Total":10,"Status":0,"Version":1}', status: 'Running', view: 'compare' });

      expect(text(panel(fixture)!.querySelector('.ov-caption'))).toBe(
        'Compare the state after step 1 (Started by OrderSubmitted) with the current state',
      );
      expect(Array.from(panel(fixture)!.querySelectorAll('.insp-note')).map(text)).toContain(
        "None of the saga's own fields changed since then.",
      );
    });

    it('is disabled, and shows nothing, without a recorded snapshot', () => {
      const fixture = render({ entries: noSnapshots(), view: 'compare' });

      const compare = button(fixture, 'Compare')!;
      expect(compare.disabled).toBe(true);
      expect(compare.getAttribute('title')).toBe(
        'No state snapshot was recorded for this saga, so there is nothing to compare with.',
      );
      expect(compare.getAttribute('aria-pressed')).toBe('false');
      expect(panel(fixture)).toBeNull();
      expect(button(fixture, 'At start')!.disabled).toBe(false);
      expect(button(fixture, 'At end')!.disabled).toBe(false);
    });

    it('is disabled without a stored state, and says that is what is missing', () => {
      const fixture = render({ currentJson: null, view: 'compare' });

      const compare = button(fixture, 'Compare')!;
      expect(compare.disabled).toBe(true);
      expect(compare.getAttribute('title')).toBe('No state is stored for this saga, so there is nothing to compare with.');
      expect(panel(fixture)).toBeNull();
    });

    it('has no title while enabled', () => {
      const fixture = render();

      expect(button(fixture, 'Compare')!.hasAttribute('title')).toBe(false);
    });

    it('opens once the timeline brings a snapshot', () => {
      const fixture = render({ entries: [], view: 'compare' });
      expect(panel(fixture)).toBeNull();

      fixture.componentRef.setInput('history', foldTimeline(twoSteps()));
      fixture.detectChanges();

      expect(button(fixture, 'Compare')!.disabled).toBe(false);
      expect(panel(fixture)?.classList).toContain('ov-compare');
    });
  });

  describe('before the timeline has loaded', () => {
    it('says At start is loading rather than that nothing was recorded', () => {
      const fixture = render({ entries: [], historyLoaded: false, view: 'start' });

      expect(Array.from(panel(fixture)!.querySelectorAll('.ov-note')).map(text)).toEqual(["Loading the saga's history…"]);
      expect(panel(fixture)!.querySelector('.ov-caption')).toBeNull();
    });

    it('keeps Compare disabled without claiming that no snapshot was recorded', () => {
      const fixture = render({ entries: [], historyLoaded: false, view: 'compare' });

      const compare = button(fixture, 'Compare')!;
      expect(compare.disabled).toBe(true);
      expect(compare.hasAttribute('title')).toBe(false);
      expect(panel(fixture)).toBeNull();
    });

    it('shows the loaded history once it arrives', () => {
      const fixture = render({ entries: [], historyLoaded: false, view: 'compare' });

      fixture.componentRef.setInput('history', foldTimeline(twoSteps()));
      fixture.componentRef.setInput('historyLoaded', true);
      fixture.detectChanges();

      expect(panel(fixture)?.classList).toContain('ov-compare');
    });
  });

  describe('without the sagas.data permission', () => {
    it.each<DataView | null>([null, 'start', 'end', 'compare'])('disables every button and opens nothing (view %s)', (view) => {
      const fixture = render({ canViewData: false, view });

      expect(buttons(fixture).map((b) => text(b))).toEqual(['At start', 'At end', 'Compare']);
      expect(buttons(fixture).every((b) => b.disabled && b.getAttribute('aria-pressed') === 'false')).toBe(true);
      expect(text(fixture.nativeElement.querySelector('.ov-bar .muted'))).toBe(
        'Saga data is hidden for your role. It needs the sagas.data permission.',
      );
      expect(panel(fixture)).toBeNull();
      expect(fixture.nativeElement.querySelector('pre')).toBeNull();
    });

    it('says nothing about permissions to a viewer who has it', () => {
      const fixture = render();

      expect(fixture.nativeElement.querySelector('.ov-bar .muted')).toBeNull();
    });
  });
});
