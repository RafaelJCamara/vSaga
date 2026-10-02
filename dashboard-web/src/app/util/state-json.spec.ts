import {
  formatStateJson,
  isOmissionMarker,
  parseStateJson,
  prettyJson,
  SAGA_KINDS,
  SAGA_STATUSES,
} from './state-json';

describe('state-json', () => {
  describe('formatStateJson', () => {
    // These three moved here from saga-detail.spec.ts with the prettyDataJson getter's body.
    it('pretty-prints valid JSON', () => {
      expect(formatStateJson('{"a":1}')).toBe(JSON.stringify({ a: 1 }, null, 2));
    });

    it('returns the raw string when the JSON is invalid', () => {
      expect(formatStateJson('not json')).toBe('not json');
    });

    it('returns an empty string when there is no data', () => {
      expect(formatStateJson(null)).toBe('');
      expect(formatStateJson(undefined)).toBe('');
      expect(formatStateJson('')).toBe('');
    });

    it('reads top-level numeric Kind and Status as names', () => {
      const text = formatStateJson('{"Kind":1,"Status":2,"OrderId":"o-1"}');
      expect(text).toBe(
        JSON.stringify({ Kind: 'Choreographed', Status: 'Failed', OrderId: 'o-1' }, null, 2),
      );
    });

    it('leaves out-of-range and non-integer enum values as numbers', () => {
      expect(JSON.parse(formatStateJson('{"Kind":2,"Status":7}'))).toEqual({ Kind: 2, Status: 7 });
      expect(JSON.parse(formatStateJson('{"Kind":-1,"Status":1.5}'))).toEqual({
        Kind: -1,
        Status: 1.5,
      });
    });

    it('leaves Kind and Status that are already names untouched', () => {
      expect(JSON.parse(formatStateJson('{"Kind":"Orchestrated","Status":"Running"}'))).toEqual({
        Kind: 'Orchestrated',
        Status: 'Running',
      });
    });

    it('leaves a nested Kind or Status alone: it belongs to the saga model', () => {
      const text = formatStateJson('{"Status":0,"Shipment":{"Kind":1,"Status":3}}');
      expect(JSON.parse(text)).toEqual({ Status: 'Running', Shipment: { Kind: 1, Status: 3 } });
    });

    it('pretty-prints JSON that is not an object as it is', () => {
      expect(formatStateJson('[0,1]')).toBe(JSON.stringify([0, 1], null, 2));
      expect(formatStateJson('null')).toBe('null');
    });
  });

  describe('prettyJson', () => {
    it('pretty-prints a payload without the enum mapping', () => {
      expect(prettyJson('{"Status":2}')).toBe(JSON.stringify({ Status: 2 }, null, 2));
    });

    it('returns the raw text when invalid and an empty string when absent', () => {
      expect(prettyJson('<xml/>')).toBe('<xml/>');
      expect(prettyJson(null)).toBe('');
    });
  });

  describe('parseStateJson', () => {
    it('reports nothing stored as empty', () => {
      expect(parseStateJson(null)).toEqual({ kind: 'empty' });
      expect(parseStateJson(undefined)).toEqual({ kind: 'empty' });
      expect(parseStateJson('')).toEqual({ kind: 'empty' });
    });

    it('keeps invalid JSON as its raw text', () => {
      expect(parseStateJson('{oops')).toEqual({ kind: 'invalid', raw: '{oops' });
    });

    it('returns the value with top-level enums as names', () => {
      expect(parseStateJson('{"Kind":0,"Status":5,"Total":12.5}')).toEqual({
        kind: 'value',
        value: { Kind: 'Orchestrated', Status: 'TimedOut', Total: 12.5 },
      });
    });

    it('recognises the MongoDB payload marker', () => {
      expect(
        parseStateJson('{"$vsagaPayloadOmitted":true,"bytes":20000000,"limit":16000000}'),
      ).toEqual({
        kind: 'omitted',
        bytes: 20000000,
        limit: 16000000,
        budget: null,
      });
    });

    it('recognises the engine snapshot marker, per-snapshot cap and per-saga budget', () => {
      expect(parseStateJson('{"$vsagaStateOmitted":true,"bytes":300000,"limit":262144}')).toEqual({
        kind: 'omitted',
        bytes: 300000,
        limit: 262144,
        budget: null,
      });
      expect(parseStateJson('{"$vsagaStateOmitted":true,"bytes":4096,"budget":1048576}')).toEqual({
        kind: 'omitted',
        bytes: 4096,
        limit: null,
        budget: 1048576,
      });
    });

    it('reports a marker without sizes with null sizes', () => {
      expect(parseStateJson('{"$vsagaStateOmitted":true}')).toEqual({
        kind: 'omitted',
        bytes: null,
        limit: null,
        budget: null,
      });
    });

    it('treats look-alikes as ordinary values', () => {
      // Not true, not the vSaga prefix, nested, or an array: all saga data, not markers.
      for (const json of [
        '{"$vsagaStateOmitted":false,"bytes":1}',
        '{"$vsagaStateOmitted":"true"}',
        '{"$otherOmitted":true}',
        '{"vsagaStateOmitted":true}',
        '{"Inner":{"$vsagaStateOmitted":true}}',
        '[{"$vsagaStateOmitted":true}]',
      ]) {
        expect(parseStateJson(json).kind, json).toBe('value');
      }
    });
  });

  it('isOmissionMarker needs an own $vsaga…Omitted key set to true', () => {
    expect(isOmissionMarker({ $vsagaPayloadOmitted: true })).toBe(true);
    expect(isOmissionMarker({ $vsagaOmitted: true })).toBe(true);
    expect(isOmissionMarker(Object.create({ $vsagaStateOmitted: true }))).toBe(false);
    expect(isOmissionMarker(null)).toBe(false);
    expect(isOmissionMarker('$vsagaStateOmitted')).toBe(false);
  });

  it('isOmissionMarker leaves a plain record readable in its false branch', () => {
    // Compile-level: a guard typed `value is Record<string, unknown>` would narrow `state` to
    // never in the else branch, and the assertion below would not compile.
    const state: Record<string, unknown> = JSON.parse('{"Status":"Running"}');
    let status: unknown = null;
    if (isOmissionMarker(state)) {
      status = state['bytes'];
    } else {
      const notNever: [typeof state] extends [never] ? 'narrowed to never' : true = true;
      expect(notNever).toBe(true);
      status = state['Status'];
    }
    expect(status).toBe('Running');
  });

  it('keeps the enum name lists in C# declaration order', () => {
    expect(SAGA_KINDS).toEqual(['Orchestrated', 'Choreographed']);
    expect(SAGA_STATUSES).toEqual([
      'Running',
      'Completed',
      'Failed',
      'Compensating',
      'Compensated',
      'TimedOut',
      'Cancelled',
    ]);
  });
});
