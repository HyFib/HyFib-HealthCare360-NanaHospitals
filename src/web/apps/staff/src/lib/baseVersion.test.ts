import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { followIfUnchanged, useBaseVersion } from './baseVersion';

/** An anaesthesia-like record: the editor edits `notes`; `vitals` are charted elsewhere on the screen. */
interface Chart {
  version: number;
  notes: string | null;
  vitals: number[];
}

const FILLED: Chart = { version: 7, notes: 'Stable', vitals: [] };

/** An editor filled from `base`, on a screen that hands it the record as the user's own other changes returned it. */
function editor(base: Chart | null = FILLED) {
  return renderHook(
    ({ filled, own }: { filled: number | null; own: Chart | null }) =>
      useBaseVersion(filled, own, (returned) =>
        followIfUnchanged(base ?? { notes: null }, returned, ['notes'] as const),
      ),
    { initialProps: { filled: base?.version ?? null, own: null as Chart | null } },
  );
}

describe('the version an editor saves on (REL-04)', () => {
  it('stays the version it was filled from when the record is read again', () => {
    const hook = editor();

    hook.rerender({ filled: 9, own: null });

    expect(hook.result.current[0]).toBe(7);
  });

  it("moves on with the editor's own save", () => {
    const hook = editor();

    act(() => hook.result.current[1](8));

    expect(hook.result.current[0]).toBe(8);
  });

  it("follows the user's change elsewhere while the record it returns holds the editor's fields as filled", () => {
    const hook = editor();

    hook.rerender({ filled: 7, own: { version: 8, notes: 'Stable', vitals: [72] } });
    expect(hook.result.current[0]).toBe(8);
    // Someone else's vitals came in between: they are not the editor's fields, so it follows all the same.
    hook.rerender({ filled: 7, own: { version: 11, notes: 'Stable', vitals: [72, 80, 76] } });
    expect(hook.result.current[0]).toBe(11);
  });

  it("stays behind when the record it returns holds someone else's change to the editor's fields", () => {
    const hook = editor();

    hook.rerender({ filled: 7, own: { version: 10, notes: 'Bradycardia at 10:40', vitals: [72] } });

    expect(hook.result.current[0]).toBe(7);
  });

  it('follows a change that created the record the editor was opened without, while nothing else was saved on it', () => {
    const created = editor(null);
    created.rerender({ filled: null, own: { version: 3, notes: null, vitals: [72] } });
    expect(created.result.current[0]).toBe(3);

    const taken = editor(null);
    taken.rerender({ filled: null, own: { version: 4, notes: 'Difficult airway', vitals: [72] } });
    expect(taken.result.current[0]).toBeNull();
  });
});

describe('whether a returned record still holds what an editor was based on', () => {
  it('compares the given fields, values inside lists and objects included', () => {
    const base = { version: 1, drugs: [{ drug: 'Propofol', dose: '120 mg' }], vitals: [72] };

    expect(followIfUnchanged(base, { ...base, version: 2, vitals: [72, 80] }, ['drugs'])).toBe(true);
    expect(
      followIfUnchanged(base, { ...base, drugs: [{ drug: 'Propofol', dose: '150 mg' }] }, ['drugs', 'vitals']),
    ).toBe(false);
  });

  it("compares every field of the base when none are given (an editor's values as the form holds them)", () => {
    const filled = { summary: 'Fit', recommendations: '' };

    expect(followIfUnchanged(filled, { summary: 'Fit', recommendations: '' })).toBe(true);
    expect(followIfUnchanged(filled, { summary: 'Fit', recommendations: 'Repeat lipids in 3 months' })).toBe(false);
  });
});
