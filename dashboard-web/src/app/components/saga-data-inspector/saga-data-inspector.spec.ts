import { ComponentFixture, TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { SnapshotState } from '../../util/saga-transitions';
import { SagaDataInspector } from './saga-data-inspector';

interface Inputs {
  state?: SnapshotState;
  afterJson?: string | null;
  beforeJson?: string | null;
  beforeLabel?: string | null;
  message?: { label: string; json: string } | null;
}

describe('SagaDataInspector', () => {
  function render(inputs: Inputs = {}): ComponentFixture<SagaDataInspector> {
    const fixture = TestBed.createComponent(SagaDataInspector);
    fixture.componentRef.setInput('state', inputs.state ?? 'recorded');
    for (const name of ['afterJson', 'beforeJson', 'beforeLabel', 'message'] as const) {
      if (inputs[name] !== undefined) fixture.componentRef.setInput(name, inputs[name]);
    }
    fixture.detectChanges();
    return fixture;
  }

  const text = (el: Element | null | undefined) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  function button(fixture: ComponentFixture<SagaDataInspector>, label: string): HTMLButtonElement | undefined {
    const buttons = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>);
    return buttons.find((b) => text(b) === label);
  }

  function notes(fixture: ComponentFixture<SagaDataInspector>): string[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.insp-note') as NodeListOf<Element>).map(text);
  }

  describe('a step without a state to show', () => {
    it.each<[SnapshotState, string]>([
      ['withheld', 'Saga data is hidden for your role. It needs the sagas.data permission.'],
      ['not-persisted', 'This step did not persist a new state, so the data is unchanged.'],
      ['pending', 'Not recorded yet. The step may still be committing; this view refreshes by itself.'],
      [
        'missing',
        'No snapshot was recorded for this step: the saga ran before snapshots existed, they are switched off, ' +
          'or the step lost a concurrent update and never committed.',
      ],
    ])('explains a %s snapshot', (state, note) => {
      const fixture = render({ state });

      expect(notes(fixture)).toEqual([note]);
      expect(fixture.nativeElement.querySelector('[role="group"]')).toBeNull();
      expect(fixture.nativeElement.querySelector('pre')).toBeNull();
    });

    it('gives the size and the limit of a state too large to snapshot', () => {
      const fixture = render({ state: 'omitted', afterJson: '{"$vsagaStateOmitted":true,"bytes":300000,"limit":262144}' });

      expect(notes(fixture)).toEqual(['The state was too large to snapshot at this step (300000 bytes, limit 262144).']);
    });

    it("reads MongoDB's payload marker the same way", () => {
      const fixture = render({ state: 'omitted', afterJson: '{"$vsagaPayloadOmitted":true,"bytes":20000000,"limit":15728640}' });

      expect(notes(fixture)).toEqual(['The state was too large to snapshot at this step (20000000 bytes, limit 15728640).']);
    });

    it('leaves out the figures a marker does not carry', () => {
      const fixture = render({ state: 'omitted', afterJson: '{"$vsagaStateOmitted":true}' });

      expect(notes(fixture)).toEqual(['The state was too large to snapshot at this step.']);
    });

    it("says the saga's snapshot budget was used up for a budget marker, not that the state was too large", () => {
      const fixture = render({ state: 'omitted', afterJson: '{"$vsagaStateOmitted":true,"bytes":2048,"budget":1048576}' });

      expect(notes(fixture)).toEqual([
        "The state was not snapshotted at this step: the saga's snapshot budget of 1048576 bytes was used up " +
          '(this state is 2048 bytes). Snapshots after a failed step are still recorded in full.',
      ]);
      expect(notes(fixture)[0]).not.toContain('too large');
    });

    it('still shows the message that ran the step', () => {
      const fixture = render({ state: 'missing', message: { label: 'PaymentCaptured', json: '{"Amount":10}' } });

      expect(text(fixture.nativeElement.querySelector('.insp-caption'))).toBe('Message PaymentCaptured');
      expect(fixture.nativeElement.querySelector('pre')?.textContent).toBe(JSON.stringify({ Amount: 10 }, null, 2));
    });
  });

  describe('a recorded state', () => {
    const before = '{"Status":0,"Version":3,"UpdatedAtUtc":"2026-01-01T00:00:00Z","OrderId":"o-1","Total":10,"Note":"x"}';
    const after = '{"Status":2,"Version":4,"UpdatedAtUtc":"2026-01-01T00:00:01Z","OrderId":"o-1","Total":12,"Paid":true}';

    function rows(fixture: ComponentFixture<SagaDataInspector>): string[][] {
      return Array.from(fixture.nativeElement.querySelectorAll('.insp-diff tbody tr') as NodeListOf<Element>).map((tr) =>
        Array.from(tr.children).map(text),
      );
    }

    it('opens on the changes against the earlier snapshot, which it names', () => {
      const fixture = render({ afterJson: after, beforeJson: before, beforeLabel: 'the state after step 1 (Started by OrderSubmitted)' });

      expect(fixture.componentInstance.view()).toBe('changes');
      expect(button(fixture, 'Changes')?.getAttribute('aria-pressed')).toBe('true');
      expect(button(fixture, 'Full state')?.getAttribute('aria-pressed')).toBe('false');
      expect(notes(fixture)[0]).toBe('Compared with the state after step 1 (Started by OrderSubmitted)');
      expect(fixture.nativeElement.querySelector('pre')).toBeNull();
    });

    it('lists each changed field with a marker a screen reader can read, and its values before and after', () => {
      const fixture = render({ afterJson: after, beforeJson: before });

      expect(rows(fixture)).toEqual([
        ['~Changed: Status', '"Running"', '"Failed"'],
        ['~Changed: Total', '10', '12'],
        ['+Added: Paid', '—', 'true'],
        ['−Removed: Note', '"x"', '—'],
      ]);
      const mark = fixture.nativeElement.querySelector('.insp-diff tbody th .insp-mark');
      expect(mark.getAttribute('aria-hidden')).toBe('true');
      expect(fixture.nativeElement.querySelector('.insp-diff tbody th .sr-only').textContent).toBe('Changed:');
    });

    it('reads a status change by name: Running → Failed, not 0 → 2', () => {
      const fixture = render({ afterJson: '{"Status":2}', beforeJson: '{"Status":0}' });

      expect(rows(fixture)).toEqual([['~Changed: Status', '"Running"', '"Failed"']]);
    });

    it("puts the engine's bookkeeping on one muted line instead of the table", () => {
      const fixture = render({ afterJson: after, beforeJson: before });

      const bookkeeping = fixture.nativeElement.querySelector('.insp-bookkeeping');
      expect(bookkeeping.classList).toContain('muted');
      expect(text(bookkeeping)).toBe(
        'Engine bookkeeping also changed: Version 3 → 4, UpdatedAtUtc 2026-01-01T00:00:00Z → 2026-01-01T00:00:01Z',
      );
      expect(rows(fixture).map((r) => r[0])).not.toContain('~Changed: Version');
    });

    it("says so when only the engine's bookkeeping changed", () => {
      const fixture = render({ afterJson: '{"Version":2,"A":1}', beforeJson: '{"Version":1,"A":1}' });

      expect(fixture.nativeElement.querySelector('.insp-diff')).toBeNull();
      expect(notes(fixture)).toContain("None of the saga's own fields changed in this step.");
      expect(notes(fixture)).toContain('Engine bookkeeping also changed: Version 1 → 2');
    });

    it('counts only the rows it shows when the changes are cut off', () => {
      // 250 changed fields plus the two bookkeeping ones: the diff stops at 200 changes, two of
      // which are Version and UpdatedAtUtc, so the table holds 198.
      const fields = (value: number) =>
        Object.fromEntries(Array.from({ length: 250 }, (_, i) => [`F${String(i).padStart(3, '0')}`, value]));
      const fixture = render({
        beforeJson: JSON.stringify({ Version: 1, UpdatedAtUtc: '2026-01-01T00:00:00Z', ...fields(0) }),
        afterJson: JSON.stringify({ Version: 2, UpdatedAtUtc: '2026-01-01T00:00:01Z', ...fields(1) }),
      });

      expect(fixture.componentInstance.diff()?.truncated).toBe(true);
      expect(rows(fixture)).toHaveLength(198);
      expect(notes(fixture)).toContain('Showing the first 198 changes. Full state shows the rest.');
    });

    it('opens on the full state, with Changes disabled, when there is nothing to compare with', () => {
      const fixture = render({ afterJson: '{"Status":1,"Kind":0}' });

      expect(fixture.componentInstance.view()).toBe('state');
      expect(button(fixture, 'Changes')?.disabled).toBe(true);
      expect(button(fixture, 'Full state')?.getAttribute('aria-pressed')).toBe('true');
      expect(fixture.nativeElement.querySelector('pre').textContent).toBe(
        JSON.stringify({ Status: 'Completed', Kind: 'Orchestrated' }, null, 2),
      );
    });

    it('switches between the changes, the full state and the message', () => {
      const fixture = render({
        afterJson: '{"A":2}',
        beforeJson: '{"A":1}',
        message: { label: 'PaymentCaptured', json: '{"Amount":10}' },
      });

      button(fixture, 'Full state')!.click();
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('pre').textContent).toBe(JSON.stringify({ A: 2 }, null, 2));
      expect(fixture.nativeElement.querySelector('.insp-diff')).toBeNull();

      button(fixture, 'Message')!.click();
      fixture.detectChanges();
      expect(button(fixture, 'Message')?.getAttribute('aria-pressed')).toBe('true');
      expect(text(fixture.nativeElement.querySelector('.insp-caption'))).toBe('Message PaymentCaptured');
      expect(fixture.nativeElement.querySelector('pre').textContent).toBe(JSON.stringify({ Amount: 10 }, null, 2));
    });

    it('offers no Message view when no payload was recorded', () => {
      const fixture = render({ afterJson: '{"A":2}' });

      expect(button(fixture, 'Message')).toBeUndefined();
    });
  });

  describe('Copy JSON', () => {
    let writeText: ReturnType<typeof vi.fn>;

    beforeEach(() => {
      writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    });

    afterEach(() => {
      Reflect.deleteProperty(navigator, 'clipboard');
    });

    it('copies the stored text unchanged, not the re-serialised view', async () => {
      // Past 2^53 and with a trailing zero: JSON.parse would print 12345678901234567000 and 1.5.
      const raw = '{"Id":12345678901234567890,"Rate":1.50}';
      const fixture = render({ afterJson: raw });

      button(fixture, 'Copy JSON')!.click();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(writeText).toHaveBeenCalledWith(raw);
      expect(text(fixture.nativeElement.querySelector('[role="status"]'))).toBe('Copied');
    });

    it("copies the message's stored text from the Message view", () => {
      const fixture = render({ afterJson: '{"A":1}', message: { label: 'PaymentCaptured', json: '{"Amount":10.0}' } });

      button(fixture, 'Message')!.click();
      fixture.detectChanges();
      button(fixture, 'Copy JSON')!.click();

      expect(writeText).toHaveBeenCalledWith('{"Amount":10.0}');
    });

    it('is not offered where the browser has no clipboard', () => {
      Reflect.deleteProperty(navigator, 'clipboard');
      const fixture = render({ afterJson: '{"A":1}' });

      expect(button(fixture, 'Copy JSON')).toBeUndefined();
      expect(button(fixture, 'Full state')).toBeDefined();
    });
  });
});
