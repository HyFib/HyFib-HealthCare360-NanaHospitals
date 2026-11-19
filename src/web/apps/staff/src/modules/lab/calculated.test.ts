import { describe, expect, it } from 'vitest';
import { calculationNote, enteredLabel } from './calculated';

const NOTE = 'Calculated by HC360: Total protein − Albumin = 7.2 − 4.1 = 3.1 g/dL.';

describe('calculated results', () => {
  it('shows the verifier the formula with the inputs it was worked from', () => {
    const line = {
      status: 'Entered',
      isCalculated: true,
      formula: 'Total protein − Albumin',
      heldReasons: [NOTE],
    } as const;

    expect(calculationNote({ ...line, heldReasons: [...line.heldReasons] })).toBe(NOTE);
    expect(enteredLabel({ ...line, heldReasons: [NOTE], instrumentCode: null })).toBe('Calculated');
  });

  it('says a test awaiting its inputs needs no entry, and why one was not calculated', () => {
    const pending = { status: 'Pending', isCalculated: false, formula: 'Triglycerides ÷ 5 (Friedewald)' } as const;

    expect(calculationNote({ ...pending, heldReasons: [] })).toBe(
      'Calculated by HC360 when its inputs are in: Triglycerides ÷ 5 (Friedewald).',
    );
    const why = 'Not calculated: The triglycerides are 452 mg/dL: from 400 up, VLDL is not calculated (Friedewald).';
    expect(calculationNote({ ...pending, heldReasons: [why] })).toBe(why);
  });

  it('says nothing of a measured test, or of a value typed in or measured for a calculated one', () => {
    expect(calculationNote({ status: 'Entered', isCalculated: false, formula: null, heldReasons: [] })).toBeNull();
    expect(
      calculationNote({ status: 'Entered', isCalculated: false, formula: 'Albumin ÷ Globulin', heldReasons: [] }),
    ).toBeNull();
    expect(
      enteredLabel({ status: 'Entered', isCalculated: false, formula: null, heldReasons: [], instrumentCode: 'C150' }),
    ).toBe('From C150');
    expect(enteredLabel({ status: 'Entered', heldReasons: [], instrumentCode: null })).toBe('Entered');
  });
});
