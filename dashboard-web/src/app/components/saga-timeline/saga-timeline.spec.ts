import { ComponentFixture, TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { SagaLogEntry } from '../../models/saga.model';
import {
  deliveryExhausted,
  failed,
  httpCall,
  httpReply,
  numbered,
  persisted,
  received,
  retryRequested,
  sagaCompleted,
  started,
  succeeded,
  T0,
  timedOutInvoice,
} from '../../testing/timeline-fixtures';
import { PENDING_SNAPSHOT_MS, foldTimeline } from '../../util/saga-transitions';
import { formatRecordedAt, timezoneLabel } from '../../util/time-format';
import { SagaTimeline } from './saga-timeline';

// Local texts depend on the zone of the machine running the spec (CI runs in UTC, developer
// machines do not), so they are compared with the formatter's own output; the datetime attribute,
// the UTC title and the offset are exact.
describe('SagaTimeline', () => {
  function render(
    entries: SagaLogEntry[],
    inputs: { live?: boolean; focusedSequence?: number | null; canViewData?: boolean } = {},
  ): ComponentFixture<SagaTimeline> {
    const fixture = TestBed.createComponent(SagaTimeline);
    fixture.componentRef.setInput('history', foldTimeline(entries));
    if (inputs.live !== undefined) fixture.componentRef.setInput('live', inputs.live);
    if (inputs.focusedSequence !== undefined) {
      fixture.componentRef.setInput('focusedSequence', inputs.focusedSequence);
    }
    if (inputs.canViewData !== undefined) fixture.componentRef.setInput('canViewData', inputs.canViewData);
    fixture.detectChanges();
    return fixture;
  }

  const text = (el: Element | null | undefined) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  /** A step header's number, title and outcome, without its Data toggle. */
  const headText = (head: Element) => {
    const copy = head.cloneNode(true) as Element;
    copy.querySelector('.tl-data')?.remove();
    return text(copy);
  };

  /** A started saga with one more message: two steps, five rows, two snapshots. */
  function twoSteps(): SagaLogEntry[] {
    return numbered([
      started('m0'),
      received('m0', 'OrderSubmitted'),
      succeeded('m0', 'Initial', 'Submitted'),
      persisted('m0'),
      received('m1', 'PaymentCaptured', { sourceService: 'payments' }),
      succeeded('m1', 'Submitted', 'Paid'),
      persisted('m1'),
    ]);
  }

  it('heads every step with its number, its title and how it ended', () => {
    const el: HTMLElement = render(twoSteps()).nativeElement;

    const heads = Array.from(el.querySelectorAll('.tl-head')).map(headText);
    expect(heads).toEqual([
      'Step 1 Started by OrderSubmitted · succeeded',
      'Step 2 PaymentCaptured · succeeded',
    ]);
  });

  describe('the failed step', () => {
    function renderMarked(entries: SagaLogEntry[], failure: number | null, replay: number | null) {
      const fixture = TestBed.createComponent(SagaTimeline);
      fixture.componentRef.setInput('history', foldTimeline(entries));
      fixture.componentRef.setInput('failureSequence', failure);
      fixture.componentRef.setInput('replaySequence', replay);
      fixture.detectChanges();
      const el: HTMLElement = fixture.nativeElement;
      return Array.from(el.querySelectorAll('.tl-step')).map((step) => ({
        failedHere: step.classList.contains('tl-step--failed-here'),
        markers: Array.from(step.querySelectorAll('.tl-head .tl-marker')).map(text),
      }));
    }

    /** A business failure: PaymentFailed (#4, step 2) failed the saga, SagaCompleted #6, in the same step. */
    function businessFailure(): SagaLogEntry[] {
      return numbered([
        started('m0'),
        succeeded('m0', 'Initial', 'Gathering'),
        persisted('m0'),
        received('c3', 'PaymentFailed'),
        succeeded('c3', 'Gathering', 'Failed'),
        sagaCompleted('Failed', 'c3'),
        persisted('c3', '{"Status":2}'),
      ]);
    }

    it('marks only "Failed here" when the step a retry re-runs is the failed step', () => {
      expect(renderMarked(businessFailure(), 6, 4)).toEqual([
        { failedHere: false, markers: [] },
        { failedHere: true, markers: ['Failed here'] },
      ]);
    });

    it('marks the step that entered the timed-out state "Re-run starts here"', () => {
      expect(renderMarked(timedOutInvoice(), 66, 61)).toEqual([
        { failedHere: false, markers: ['Re-run starts here'] },
        { failedHere: true, markers: ['Failed here'] },
      ]);
    });

    it('marks nothing without a plan, or for entries the timeline does not hold', () => {
      expect(renderMarked(timedOutInvoice(), null, null).every((s) => !s.failedHere && s.markers.length === 0)).toBe(true);
      expect(renderMarked(timedOutInvoice(), 999, 998).every((s) => !s.failedHere && s.markers.length === 0)).toBe(true);
    });

    it('says "Failed here" in words, inside the step header', () => {
      const fixture = TestBed.createComponent(SagaTimeline);
      fixture.componentRef.setInput('history', foldTimeline(timedOutInvoice()));
      fixture.componentRef.setInput('failureSequence', 66);
      fixture.detectChanges();

      const heads = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.tl-head')).map(headText);
      expect(heads).toEqual(['Step 1 Started by InvoiceIssued · succeeded', 'Step 2 Timeout in AwaitingArchival · succeeded Failed here']);
    });
  });

  it('labels each row with its ordinal, type, states, message, service and recorded time', () => {
    const entries = twoSteps();
    const el: HTMLElement = render(entries).nativeElement;

    const row = el.querySelectorAll('.tl-row')[3];
    const received = entries[4];
    const expected = formatRecordedAt(received.occurredAtUtc, T0);
    expect(text(row)).toBe(
      `#4 MessageReceived PaymentCaptured from payments Recorded at ${expected.local} +0.400 s since the saga's first entry Show on map`,
    );

    const time = row.querySelector('time')!;
    expect(time.getAttribute('datetime')).toBe(received.occurredAtUtc);
    expect(time.getAttribute('title')).toBe('2026-01-01 00:00:00.400 UTC');
    expect(text(row.querySelector('.tl-offset'))).toBe("+0.400 s since the saga's first entry");
    expect(row.querySelector('.tl-offset')?.getAttribute('title')).toBe("Since the saga's first entry");

    expect(text(el.querySelectorAll('.tl-row')[2])).toContain('Initial → Submitted');
  });

  it('reads the terminal entry as SagaFinalized', () => {
    const el: HTMLElement = render(
      numbered([started('m0'), succeeded('m0', 'Initial', 'Done'), sagaCompleted('Done', 'm0')]),
    ).nativeElement;

    const types = Array.from(el.querySelectorAll('.entry-type')).map(text);
    expect(types).toEqual(['SagaStarted', 'StepSucceeded', 'SagaFinalized']);
  });

  it('never lists a state snapshot as a row', () => {
    const el: HTMLElement = render(twoSteps()).nativeElement;

    expect(el.querySelectorAll('.tl-row').length).toBe(5);
    expect(el.textContent).not.toContain('StatePersisted');
    const ordinals = Array.from(el.querySelectorAll('.tl-row .mono:first-child')).map(text);
    expect(ordinals).toEqual(['#1', '#2', '#3', '#4', '#5']);
  });

  it('keeps a .CallHttp request and its reply inside the step that made the call', () => {
    const el: HTMLElement = render(
      numbered([
        started('m0', 'LookingUp'),
        received('m0', 'OrderSubmitted'),
        httpCall('c1', 'm0'),
        httpReply('r1', 'c1'),
        succeeded('m0', 'LookingUp', 'Done'),
      ]),
    ).nativeElement;

    expect(el.querySelectorAll('.tl-step').length).toBe(1);
    const rows = Array.from(el.querySelectorAll('.tl-row')).map(text);
    expect(rows[2]).toContain('POST http://loyalty/lookup');
    expect(rows[2]).toContain('to loyalty');
    expect(rows[3]).toContain('200 OK');
  });

  it('names the viewer zone once, in the hint above the steps', () => {
    const el: HTMLElement = render(twoSteps()).nativeElement;

    const hints = el.querySelectorAll('.tl-hint');
    expect(hints.length).toBe(1);
    expect(text(hints[0])).toBe(
      `Times show when the engine recorded each entry, in your local time (${timezoneLabel(new Date(T0))}). ` +
        "Hover a time for UTC. The offset counts from the saga's first entry.",
    );
  });

  it('makes every row a native button that names its entry and asks for the map', () => {
    const el: HTMLElement = render(twoSteps()).nativeElement;

    const rows = Array.from(el.querySelectorAll<HTMLElement>('.tl-row'));
    expect(rows.length).toBe(5);
    for (const row of rows) {
      expect(row.tagName).toBe('BUTTON');
      expect(row.getAttribute('type')).toBe('button');
      expect(row.getAttribute('title')).toBe('Show the map as of this entry');
      expect(text(row.querySelector(':scope > .sr-only'))).toBe('Show on map');
    }
    // Sequence numbers, not ordinals: the snapshot (4) is not a row.
    expect(rows.map((r) => r.getAttribute('data-seq'))).toEqual(['1', '2', '3', '5', '6']);
    // The Data toggle is a sibling in the step header, never nested in a row.
    for (const row of rows) expect(row.querySelector('button')).toBeNull();
    expect(rows.map(text).join(' ')).not.toMatch(/\bData\b/);
  });

  it('asks for the map as of an entry when its row is clicked', () => {
    const fixture = render(twoSteps());
    const selected: number[] = [];
    fixture.componentInstance.entrySelected.subscribe((s) => selected.push(s));

    (fixture.nativeElement.querySelector('.tl-row[data-seq="5"]') as HTMLButtonElement).click();

    expect(selected).toEqual([5]);
  });

  it("asks for the map as of a step's last entry when its title is clicked", () => {
    const fixture = render(twoSteps());
    const selected: number[] = [];
    fixture.componentInstance.entrySelected.subscribe((s) => selected.push(s));

    const titles = fixture.nativeElement.querySelectorAll('.tl-head button.tl-title') as NodeListOf<HTMLButtonElement>;
    expect(titles.length).toBe(2);
    expect(titles[0].getAttribute('type')).toBe('button');
    titles[0].click();
    titles[1].click();

    // The steps' last rows; the snapshots after them (4 and 7) are not rows.
    expect(selected).toEqual([3, 6]);
  });

  it('marks the focused entry', () => {
    const el: HTMLElement = render(twoSteps(), { focusedSequence: 5 }).nativeElement;

    const focused = el.querySelectorAll('.tl-row--focused');
    expect(focused.length).toBe(1);
    expect(text(focused[0])).toMatch(/^#4 /);
    expect(focused[0].getAttribute('aria-current')).toBe('true');
    expect(el.querySelectorAll('[aria-current]').length).toBe(1);
  });

  it('moves keyboard focus to the focused entry once, and a refresh does not take it back', async () => {
    const fixture = render(twoSteps(), { focusedSequence: 5 });
    await fixture.whenStable();

    const row = fixture.nativeElement.querySelector('.tl-row[data-seq="5"]') as HTMLElement;
    expect(document.activeElement).toBe(row);

    const elsewhere = fixture.nativeElement.querySelector('.tl-row[data-seq="1"]') as HTMLElement;
    elsewhere.focus();
    fixture.componentRef.setInput('history', foldTimeline([...twoSteps()]));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(elsewhere);

    fixture.componentRef.setInput('focusedSequence', 2);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.tl-row[data-seq="2"]'));
  });

  it('calls a final step without an outcome in progress only while the saga is live', () => {
    const entries = numbered([started('m0'), succeeded('m0', 'Initial', 'Submitted'), persisted('m0'), received('m1')]);

    const live: HTMLElement = render(entries, { live: true }).nativeElement;
    expect(headText(live.querySelectorAll('.tl-head')[1])).toBe('Step 2 PaymentCaptured · in progress');

    TestBed.resetTestingModule();
    const finished: HTMLElement = render(entries, { live: false }).nativeElement;
    expect(headText(finished.querySelectorAll('.tl-head')[1])).toBe('Step 2 PaymentCaptured · no outcome recorded');
  });

  it('says who asked for a manual retry and styles a failed step and its error', () => {
    const el: HTMLElement = render(
      numbered([
        started('m0'),
        succeeded('m0', 'Initial', 'Submitted'),
        received('m1'),
        failed('m1', 'Submitted', { errorMessage: 'card declined' }),
        retryRequested(null, { sourceService: 'dashboard:alice' }),
      ]),
    ).nativeElement;

    const steps = el.querySelectorAll('.tl-step');
    expect(steps[1].classList).toContain('tl-step--failed');
    expect(steps[1].classList).toContain('tl-step');
    expect(headText(steps[2].querySelector('.tl-head')!)).toBe(
      'Step 3 Manual retry of PaymentCaptured · requested by alice',
    );

    const rows = steps[1].querySelectorAll('.tl-row');
    const failedRow = rows[rows.length - 1];
    expect(text(failedRow.querySelector('.entry-type'))).toBe('StepFailed');
    expect(text(failedRow)).toContain('Submitted → —');
    expect(text(failedRow.querySelector('.tl-error'))).toBe('card declined');
    expect(failedRow.classList).toContain('tl-row--error');
    expect(rows[0].classList).not.toContain('tl-row--error');
    expect(rows[0].querySelector('.tl-error')).toBeNull();
  });

  // The API strips error text for a viewer without sagas.data; a row still must not print one that
  // arrives (a session that is out of date, an older API) while the page says the data is hidden.
  it('prints no error text without canViewData, and keeps the row and its marking', () => {
    const entries = numbered([
      started('m0'),
      succeeded('m0', 'Initial', 'Submitted'),
      received('m1'),
      failed('m1', 'Submitted', { errorMessage: 'card declined' }),
    ]);
    const el: HTMLElement = render(entries, { canViewData: false }).nativeElement;

    expect(el.textContent).not.toContain('card declined');
    expect(el.querySelector('.tl-error')).toBeNull();
    const rows = el.querySelectorAll('.tl-step')[1].querySelectorAll('.tl-row');
    expect(text(rows[rows.length - 1].querySelector('.entry-type'))).toBe('StepFailed');
    expect(rows[rows.length - 1].classList).toContain('tl-row--error');
  });

  it('heads a step opened by a dead letter without saying dead-lettered twice', () => {
    const el: HTMLElement = render(
      numbered([started('m0'), succeeded('m0', 'Initial', 'Submitted'), deliveryExhausted('m1')]),
    ).nativeElement;

    const heads = Array.from(el.querySelectorAll('.tl-head')).map(headText);
    expect(heads[1]).toBe('Step 2 PaymentCaptured dead-lettered · never handled');
  });

  it('calls a received message that was then dead-lettered dead-lettered', () => {
    const el: HTMLElement = render(
      numbered([started('m0'), succeeded('m0', 'Initial', 'Submitted'), received('m1'), deliveryExhausted('m1')]),
    ).nativeElement;

    const heads = Array.from(el.querySelectorAll('.tl-head')).map(headText);
    expect(heads[1]).toBe('Step 2 PaymentCaptured · dead-lettered');
  });

  describe('the per-step Data toggle', () => {
    /** Two steps whose snapshots differ: step 2 (key 5) changes Status and adds Paid. */
    function withData(): SagaLogEntry[] {
      return numbered([
        started('m0'),
        received('m0', 'OrderSubmitted'),
        succeeded('m0', 'Initial', 'Submitted'),
        persisted('m0', '{"Status":0,"Total":10}'),
        received('m1', 'PaymentCaptured', { payloadJson: '{"Amount":10}' }),
        succeeded('m1', 'Submitted', 'Paid'),
        persisted('m1', '{"Status":1,"Total":10,"Paid":true}'),
      ]);
    }

    const toggles = (el: HTMLElement) => Array.from(el.querySelectorAll<HTMLButtonElement>('.tl-head .tl-data'));

    it('puts a closed Data toggle in every step header, named after its step', () => {
      const el: HTMLElement = render(withData()).nativeElement;

      const buttons = toggles(el);
      expect(buttons.map(text)).toEqual(['Data', 'Data']);
      expect(buttons[1].getAttribute('type')).toBe('button');
      expect(buttons[1].classList).toContain('btn');
      expect(buttons[1].classList).toContain('btn--quiet');
      expect(buttons[1].getAttribute('aria-expanded')).toBe('false');
      expect(buttons[1].getAttribute('aria-controls')).toBe('step-data-5');
      expect(buttons[1].getAttribute('aria-label')).toBe('Data after step 2: PaymentCaptured');
      expect(el.querySelector('app-saga-data-inspector')).toBeNull();
    });

    it("opens the step's inspector below its rows and marks the toggle expanded, and closes it again", () => {
      const fixture = render(withData());
      const el: HTMLElement = fixture.nativeElement;
      const emitted: ReadonlySet<number>[] = [];
      fixture.componentInstance.openKeys.subscribe((keys) => emitted.push(keys));

      toggles(el)[1].click();
      fixture.detectChanges();

      expect(toggles(el)[1].getAttribute('aria-expanded')).toBe('true');
      expect(toggles(el)[0].getAttribute('aria-expanded')).toBe('false');
      const step = el.querySelectorAll('.tl-step')[1];
      const inspector = step.querySelector('app-saga-data-inspector')!;
      expect(inspector.id).toBe('step-data-5');
      expect(inspector.previousElementSibling?.classList).toContain('tl-rows');
      expect(text(inspector.querySelector('.insp-note'))).toBe(
        'Compared with the state after step 1 (Started by OrderSubmitted)',
      );
      const changed = Array.from(inspector.querySelectorAll('.insp-diff tbody th')).map(text);
      expect(changed).toEqual(['~Changed: Status', '+Added: Paid']);
      expect([...emitted.at(-1)!]).toEqual([5]);

      toggles(el)[1].click();
      fixture.detectChanges();

      expect(toggles(el)[1].getAttribute('aria-expanded')).toBe('false');
      expect(el.querySelector('app-saga-data-inspector')).toBeNull();
      expect([...emitted.at(-1)!]).toEqual([]);
    });

    it('opens the inspectors the page says are open, and keeps them open across a refresh', () => {
      const fixture = render(withData());
      fixture.componentRef.setInput('openKeys', new Set([1]));
      fixture.detectChanges();

      const el: HTMLElement = fixture.nativeElement;
      expect(el.querySelector('app-saga-data-inspector')?.id).toBe('step-data-1');

      fixture.componentRef.setInput('history', foldTimeline([...withData()]));
      fixture.detectChanges();
      expect(el.querySelector('app-saga-data-inspector')?.id).toBe('step-data-1');
      expect(toggles(el)[0].getAttribute('aria-expanded')).toBe('true');
    });

    it('offers no toggles and shows no inspector without canViewData', () => {
      const fixture = render(withData(), { canViewData: false });
      fixture.componentRef.setInput('openKeys', new Set([1, 5]));
      fixture.detectChanges();

      const el: HTMLElement = fixture.nativeElement;
      expect(toggles(el)).toEqual([]);
      expect(el.querySelector('app-saga-data-inspector')).toBeNull();
      expect(el.querySelectorAll('.tl-step').length).toBe(2);
    });

    describe('a live final step still waiting for its snapshot', () => {
      afterEach(() => vi.useRealTimers());

      it('reads pending while young, then missing once the window passes, without a refresh', () => {
        vi.useFakeTimers();
        // The final step (key 4) has no snapshot yet; its newest row is at T0 + 400 ms.
        vi.setSystemTime(Date.parse(T0) + 1500);
        const entries = numbered([
          started('m0'),
          succeeded('m0', 'Initial', 'Submitted'),
          persisted('m0'),
          received('m1'),
          succeeded('m1', 'Submitted', 'Paid'),
        ]);
        const fixture = render(entries, { live: true });
        fixture.componentRef.setInput('openKeys', new Set([4]));
        fixture.detectChanges();

        const note = () => text(fixture.nativeElement.querySelector('#step-data-4 .insp-note'));
        expect(note()).toBe('Not recorded yet. The step may still be committing; this view refreshes by itself.');

        vi.advanceTimersByTime(PENDING_SNAPSHOT_MS);
        fixture.detectChanges();

        expect(note()).toMatch(/^No snapshot was recorded for this step/);
      });
    });
  });
});

