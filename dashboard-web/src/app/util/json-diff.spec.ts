import { diffJson, previewValue } from './json-diff';

describe('diffJson', () => {
  it('reports nothing for equal values', () => {
    const value = { Order: { Lines: [{ Sku: 'A', Quantity: 1 }] }, Paid: true, Note: null };
    expect(diffJson(value, structuredClone(value))).toEqual({ changes: [], truncated: false });
    expect(diffJson(3, 3)).toEqual({ changes: [], truncated: false });
  });

  it('reports a changed primitive with both sides', () => {
    expect(diffJson({ Status: 'Running' }, { Status: 'Failed' }).changes).toEqual([
      { path: 'Status', kind: 'changed', before: 'Running', after: 'Failed' },
    ]);
  });

  it('gives a change to the whole value the empty path', () => {
    expect(diffJson(1, 2).changes).toEqual([{ path: '', kind: 'changed', before: 1, after: 2 }]);
  });

  it('reports an added key with its whole subtree, in after order', () => {
    const diff = diffJson({ A: 1 }, { B: { C: [1, 2] }, A: 1, D: 'x' });
    expect(diff.changes).toEqual([
      { path: 'B', kind: 'added', after: { C: [1, 2] } },
      { path: 'D', kind: 'added', after: 'x' },
    ]);
  });

  it('reports removed keys after the keys of after', () => {
    const diff = diffJson({ Gone: { Deep: true }, A: 1, Also: 2 }, { A: 5 });
    expect(diff.changes).toEqual([
      { path: 'A', kind: 'changed', before: 1, after: 5 },
      { path: 'Gone', kind: 'removed', before: { Deep: true } },
      { path: 'Also', kind: 'removed', before: 2 },
    ]);
  });

  it('walks nested objects and arrays into a dotted path', () => {
    const before = { Order: { Lines: [{ Quantity: 1 }, { Quantity: 1 }, { Quantity: 1 }] } };
    const after = { Order: { Lines: [{ Quantity: 1 }, { Quantity: 1 }, { Quantity: 4 }] } };
    expect(diffJson(before, after).changes).toEqual([
      { path: 'Order.Lines[2].Quantity', kind: 'changed', before: 1, after: 4 },
    ]);
  });

  it('compares arrays index by index, then one change per surplus index', () => {
    expect(diffJson({ L: [1, 2] }, { L: [1, 3, 4, 5] }).changes).toEqual([
      { path: 'L[1]', kind: 'changed', before: 2, after: 3 },
      { path: 'L[2]', kind: 'added', after: 4 },
      { path: 'L[3]', kind: 'added', after: 5 },
    ]);
    expect(diffJson({ L: ['a', 'b', 'c'] }, { L: ['a'] }).changes).toEqual([
      { path: 'L[1]', kind: 'removed', before: 'b' },
      { path: 'L[2]', kind: 'removed', before: 'c' },
    ]);
  });

  it('has no move detection: an insertion at the head changes every index', () => {
    expect(diffJson([1, 2], [0, 1, 2]).changes.map((c) => c.path)).toEqual(['[0]', '[1]', '[2]']);
  });

  it('reports a type change as one change, not a walk', () => {
    expect(diffJson({ V: { A: 1 } }, { V: [1] }).changes).toEqual([
      { path: 'V', kind: 'changed', before: { A: 1 }, after: [1] },
    ]);
    expect(diffJson({ V: null }, { V: {} }).changes).toEqual([
      { path: 'V', kind: 'changed', before: null, after: {} },
    ]);
    expect(diffJson({ V: '1' }, { V: 1 }).changes).toEqual([
      { path: 'V', kind: 'changed', before: '1', after: 1 },
    ]);
  });

  it('quotes a key that is not an identifier', () => {
    const diff = diffJson(
      { 'odd key': 1, Nested: { 'a-b': 1, '2nd': 1 } },
      { 'odd key': 2, Nested: { 'a-b': 2, '2nd': 2 } },
    );
    expect(diff.changes.map((c) => c.path)).toEqual([
      '["odd key"]',
      'Nested["a-b"]',
      'Nested["2nd"]',
    ]);
  });

  it('keeps identifier keys with $ and _ dotted', () => {
    expect(diffJson({ _a: { $b: 1 } }, { _a: { $b: 2 } }).changes.map((c) => c.path)).toEqual([
      '_a.$b',
    ]);
  });

  it('stops at max and says it was truncated', () => {
    const before = Object.fromEntries(Array.from({ length: 10 }, (_, i) => [`K${i}`, i]));
    const after = Object.fromEntries(Array.from({ length: 10 }, (_, i) => [`K${i}`, i + 1]));

    const diff = diffJson(before, after, 3);

    expect(diff.truncated).toBe(true);
    expect(diff.changes.map((c) => c.path)).toEqual(['K0', 'K1', 'K2']);
  });

  it('is not truncated when the changes exactly fill max', () => {
    const diff = diffJson({ A: 1, B: 1 }, { A: 2, B: 2 }, 2);
    expect(diff.truncated).toBe(false);
    expect(diff.changes.length).toBe(2);
  });

  it('caps at 200 changes by default, deep inside arrays too', () => {
    const diff = diffJson(
      { L: Array.from({ length: 300 }, () => 0) },
      { L: Array.from({ length: 300 }, () => 1) },
    );
    expect(diff.changes.length).toBe(200);
    expect(diff.truncated).toBe(true);
    expect(diff.changes[199].path).toBe('L[199]');
  });
});

describe('previewValue', () => {
  it('writes compact JSON', () => {
    expect(previewValue({ a: [1, 'x'], b: null })).toBe('{"a":[1,"x"],"b":null}');
    expect(previewValue('Failed')).toBe('"Failed"');
    expect(previewValue(undefined)).toBe('undefined');
  });

  it('cuts long values to maxChars with an ellipsis', () => {
    const preview = previewValue('x'.repeat(500));
    expect(preview.length).toBe(120);
    expect(preview.endsWith('…')).toBe(true);
    expect(previewValue([1, 2, 3], 5)).toBe('[1,2…');
  });

  it('leaves a value that fits unchanged', () => {
    expect(previewValue([1, 2, 3], 7)).toBe('[1,2,3]');
  });
});
