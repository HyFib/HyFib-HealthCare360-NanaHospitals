import { describe, expect, it } from 'vitest';
import { emrNoteDraftName, keepNoteDraft, mergeNoteDraft } from './noteDraft';

const PATIENT = '3f2b8c1e-4a5d-4e6f-9a7b-1c2d3e4f5a6b';
const draft = { type: 'Procedure' as const, title: 'Procedure note', body: 'Suturing done.' };

function memoryStorage(initial: Record<string, string> = {}) {
  const items = new Map(Object.entries(initial));
  return {
    getItem: (key: string) => items.get(key) ?? null,
    setItem: (key: string, value: string) => void items.set(key, value),
    read: (key: string) => JSON.parse(items.get(key) ?? 'null') as { value: typeof draft; savedAt: number } | null,
  };
}

describe('EMR note draft', () => {
  it('uses the EMR composer’s draft name for a patient-level note', () => {
    expect(emrNoteDraftName(PATIENT)).toBe(`emr-note:${PATIENT}`);
  });

  it('stores a new note in the shape the composer restores', () => {
    expect(JSON.parse(mergeNoteDraft(null, draft, 1000))).toEqual({ value: draft, savedAt: 1000 });
  });

  it('never overwrites an unsaved note: the new text is added at the end', () => {
    const existing = JSON.stringify({ value: { type: 'ProgressNote', title: 'Ward round', body: 'S: pain less' }, savedAt: 1 });
    expect(JSON.parse(mergeNoteDraft(existing, draft, 2000))).toEqual({
      value: { type: 'ProgressNote', title: 'Ward round', body: 'S: pain less\n\nSuturing done.' },
      savedAt: 2000,
    });
  });

  it('replaces an empty or unreadable draft', () => {
    const empty = JSON.stringify({ value: { type: 'Other', title: '', body: '   ' }, savedAt: 1 });
    expect(JSON.parse(mergeNoteDraft(empty, draft, 3)).value).toEqual(draft);
    expect(JSON.parse(mergeNoteDraft('{not json', draft, 3)).value).toEqual(draft);
  });

  it('writes to storage and reports when storage is unavailable', () => {
    const storage = memoryStorage();
    expect(keepNoteDraft(storage, 'k', draft, 5)).toBe(true);
    expect(storage.read('k')).toEqual({ value: draft, savedAt: 5 });
    expect(keepNoteDraft(null, 'k', draft)).toBe(false);
    const full = { getItem: () => null, setItem: () => { throw new DOMException('Quota', 'QuotaExceededError'); } };
    expect(keepNoteDraft(full, 'k', draft)).toBe(false);
  });
});
