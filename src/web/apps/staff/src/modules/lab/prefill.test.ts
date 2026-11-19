import { describe, expect, it } from 'vitest';
import type { ResultSheetLine } from './api';
import { isUntouchedPrefill, prefilledEdits, prefillNote, typedEdits, withRestoredDraft } from './prefill';

// An arterial blood gas's FiO2 (N8): the sheet fills in 21 % (room air) for the user to confirm by saving, and HC360
// never assumes it. Filled in, it is an edit like a typed one, but not a draft to keep nor a reason to stay on the screen.

type Line = Pick<ResultSheetLine, 'resultId' | 'status' | 'value' | 'suggestedValue'>;

const FIO2: Line = { resultId: 'fio2', status: 'Pending', value: null, suggestedValue: '21' };
const PO2: Line = { resultId: 'po2', status: 'Pending', value: null, suggestedValue: null };

describe('values filled in for the user to confirm', () => {
  it('fills in the FiO2 of a blood gas awaiting one, for whoever enters results', () => {
    expect(prefilledEdits([FIO2, PO2], true)).toEqual({ fio2: { value: '21' } });
    expect(prefilledEdits([FIO2, PO2], false)).toEqual({});
  });

  it('fills in nothing over a FiO2 already entered, or for a test the server suggests nothing for', () => {
    expect(prefilledEdits([{ ...FIO2, status: 'Entered', value: '40' }], true)).toEqual({});
    expect(prefilledEdits([{ ...FIO2, suggestedValue: undefined }], true)).toEqual({});
  });

  it('tells the user it was filled in, until they type over it or beside it', () => {
    expect(isUntouchedPrefill(FIO2, { value: '21' })).toBe(true);
    expect(prefillNote(FIO2, { value: '21' })).toBe(
      'Filled in with 21 % (room air): change it if the patient was on oxygen. Nothing is calculated from it until it is saved.',
    );
    expect(isUntouchedPrefill(FIO2, { value: '40' })).toBe(false);
    expect(prefillNote(FIO2, { value: '40' })).toBeNull();
    expect(isUntouchedPrefill(FIO2, { value: '21', remarks: 'On a venturi mask' } as { value: string })).toBe(false);
    expect(prefillNote(PO2, { value: '21' })).toBeNull();
    expect(prefillNote(FIO2, undefined)).toBeNull();
  });

  it('keeps as a draft only what the user typed', () => {
    const edits = { fio2: { value: '21' }, po2: { value: '68.0' } };

    expect(typedEdits(edits, [FIO2, PO2])).toEqual({ po2: { value: '68.0' } });
    expect(typedEdits({ fio2: { value: '35' } }, [FIO2, PO2])).toEqual({ fio2: { value: '35' } });
  });

  it('restores a draft over what was filled in, never over what was typed since', () => {
    const current = { fio2: { value: '21' }, po2: { value: '70.0' } };
    const saved = { fio2: { value: '40' }, po2: { value: '68.0' } };

    expect(withRestoredDraft(current, saved, [FIO2, PO2])).toEqual({ fio2: { value: '40' }, po2: { value: '70.0' } });
    expect(withRestoredDraft({ fio2: { value: '21' } }, {}, [FIO2, PO2])).toEqual({ fio2: { value: '21' } });
  });
});
