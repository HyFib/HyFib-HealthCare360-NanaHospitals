import { describe, expect, it } from 'vitest';
import type { ReferenceRangeDto, ResultSheetLine } from './api';
import {
  classifyCulture,
  classifyNumeric,
  classifyText,
  deltaCheck,
  flagDisplay,
  formatDelta,
  interpretLine,
  isBenignAnalyserFlag,
  isFinalOnAnalyser,
  isReadyForBulkVerify,
  isSameUnit,
  limitsFromReferenceText,
  parseNumericResult,
  reviewHints,
  selectRange,
} from './flags';

function range(partial: Partial<ReferenceRangeDto>): ReferenceRangeDto {
  return {
    id: null,
    gender: null,
    ageFromDays: 0,
    ageToDays: null,
    low: null,
    high: null,
    criticalLow: null,
    criticalHigh: null,
    text: null,
    normalValues: null,
    criticalValues: null,
    ...partial,
  };
}

const potassium = range({ low: 3.5, high: 5.1, criticalLow: 2.5, criticalHigh: 6.5 });

function line(partial: Partial<ResultSheetLine>): ResultSheetLine {
  return {
    resultId: 'r1',
    labOrderItemId: 'i1',
    orderedTest: 'Electrolytes',
    labTestId: 't1',
    testCode: 'K',
    testName: 'Potassium',
    groupName: null,
    displayOrder: 1,
    resultType: 'Numeric',
    status: 'Pending',
    value: null,
    unit: 'mmol/L',
    decimals: 1,
    options: [],
    flag: null,
    isCritical: false,
    referenceText: '3.5 - 5.1',
    previousValue: null,
    previousResultAt: null,
    deltaPercent: null,
    deltaExceeded: false,
    autoValidated: false,
    instrumentCode: null,
    instrumentUnit: null,
    instrumentFlags: null,
    instrumentStatus: null,
    instrumentComment: null,
    heldReasons: [],
    method: null,
    remarks: null,
    organism: null,
    colonyCount: null,
    susceptibilities: [],
    enteredAt: null,
    verifiedAt: null,
    hasCriticalCall: false,
    version: 1,
    ...partial,
  };
}

describe('flag display', () => {
  it('gives a code and words for every flag', () => {
    expect(flagDisplay('High')).toMatchObject({ code: 'H', tone: 'high', critical: false });
    expect(flagDisplay('Low')).toMatchObject({ code: 'L', tone: 'low' });
    expect(flagDisplay('CriticalHigh')).toMatchObject({ code: 'HH', label: 'Critically high', critical: true });
    expect(flagDisplay('CriticalLow')).toMatchObject({ code: 'LL', critical: true });
    expect(flagDisplay('Abnormal')).toMatchObject({ code: 'A', tone: 'abnormal' });
    expect(flagDisplay('Critical')).toMatchObject({ code: 'C', critical: true });
    expect(flagDisplay('Normal')).toMatchObject({ code: '', tone: 'normal' });
    expect(flagDisplay(null)).toBeNull();
  });
});

describe('numeric parsing', () => {
  it('reads plain, signed, grouped and comparator values', () => {
    expect(parseNumericResult('12.5')).toEqual({ comparator: null, value: 12.5 });
    expect(parseNumericResult('-2.5')).toEqual({ comparator: null, value: -2.5 });
    expect(parseNumericResult('1,20,000')).toEqual({ comparator: null, value: 120000 });
    expect(parseNumericResult('<0.01')).toEqual({ comparator: '<', value: 0.01 });
    expect(parseNumericResult('≥ 600')).toEqual({ comparator: '>=', value: 600 });
    expect(parseNumericResult('.5')).toEqual({ comparator: null, value: 0.5 });
  });

  it('refuses text', () => {
    expect(parseNumericResult('positive')).toBeNull();
    expect(parseNumericResult('12..5')).toBeNull();
    expect(parseNumericResult('')).toBeNull();
    expect(parseNumericResult('1e5')).toBeNull();
  });

  it('refuses a long run of digits that is not a number at once', () => {
    // A result pasted or restored from a draft can be anything. The pattern used to backtrack over every split of the
    // digits, so a hundred thousand of them took seconds.
    const started = performance.now();

    expect(parseNumericResult('0'.repeat(100_000) + 'x')).toBeNull();

    expect(performance.now() - started).toBeLessThan(500);
  });
});

describe('numeric classification', () => {
  it('uses exclusive limits and critical limits first', () => {
    expect(classifyNumeric(potassium, { comparator: null, value: 5.1 })).toBe('Normal');
    expect(classifyNumeric(potassium, { comparator: null, value: 5.2 })).toBe('High');
    expect(classifyNumeric(potassium, { comparator: null, value: 3.4 })).toBe('Low');
    expect(classifyNumeric(potassium, { comparator: null, value: 6.54 })).toBe('CriticalHigh');
    expect(classifyNumeric(potassium, { comparator: null, value: 2.4 })).toBe('CriticalLow');
  });

  it('treats comparator results like the server', () => {
    expect(classifyNumeric(potassium, { comparator: '<', value: 1 })).toBe('CriticalLow');
    expect(classifyNumeric(potassium, { comparator: '>', value: 10 })).toBe('CriticalHigh');
    const crp = { low: null, high: 5, criticalLow: null, criticalHigh: null };
    expect(classifyNumeric(crp, { comparator: '<', value: 0.5 })).toBe('Normal');
    expect(classifyNumeric(crp, { comparator: '>', value: 200 })).toBe('High');
  });

  it('cannot flag without limits', () => {
    expect(
      classifyNumeric({ low: null, high: null, criticalLow: null, criticalHigh: null }, { comparator: null, value: 3 }),
    ).toBeNull();
  });
});

describe('reference interval selection', () => {
  const ranges = [
    range({ low: 13, high: 17, gender: 'Male', ageFromDays: 18 * 365 }),
    range({ low: 12, high: 15, gender: 'Female', ageFromDays: 18 * 365 }),
    range({ low: 11, high: 14.5, ageFromDays: 6 * 365, ageToDays: 12 * 365 }),
    range({ low: 11, high: 16 }),
  ];

  it('prefers sex-specific, then the narrowest age band', () => {
    expect(selectRange(ranges, 'Male', 40 * 365)?.low).toBe(13);
    expect(selectRange(ranges, 'Female', 40 * 365)?.low).toBe(12);
    expect(selectRange(ranges, 'Female', 10 * 365)?.high).toBe(14.5);
  });

  it('uses only all-age intervals when the age is unknown', () => {
    expect(selectRange(ranges, 'Male', null)?.high).toBe(16);
    expect(selectRange([ranges[0]!], 'Male', null)).toBeNull();
  });
});

describe('answer and culture flags', () => {
  const answers = range({ normalValues: ['Non-reactive'], criticalValues: ['Reactive'] });
  it('flags answers from the lists', () => {
    expect(classifyText(answers, 'non-reactive')).toBe('Normal');
    expect(classifyText(answers, 'Reactive')).toBe('Critical');
    expect(classifyText(answers, 'Equivocal')).toBe('Abnormal');
    expect(classifyText(null, 'Anything')).toBeNull();
  });

  it('marks isolated organisms abnormal and listed ones critical', () => {
    const culture = range({ normalValues: ['No growth'], criticalValues: ['Salmonella typhi'] });
    expect(classifyCulture(culture, 'No growth', null)).toBe('Normal');
    expect(classifyCulture(culture, 'Growth', 'E. coli')).toBe('Abnormal');
    expect(classifyCulture(culture, 'Growth', 'salmonella typhi')).toBe('Critical');
  });
});

describe('reference text fallback', () => {
  it('reads intervals printed on the sheet', () => {
    expect(limitsFromReferenceText('13.0 - 17.0')).toMatchObject({ low: 13, high: 17 });
    expect(limitsFromReferenceText('1,50,000 - 4,50,000')).toMatchObject({ low: 150000, high: 450000 });
    expect(limitsFromReferenceText('Up to 40')).toMatchObject({ low: null, high: 40 });
    expect(limitsFromReferenceText('3.5 and above')).toMatchObject({ low: 3.5, high: null });
    expect(limitsFromReferenceText('Non-reactive')).toBeNull();
    expect(limitsFromReferenceText(null)).toBeNull();
  });
});

describe('delta check', () => {
  it('computes the percentage change like the server', () => {
    expect(deltaCheck(6, 4, 50)).toEqual({ percent: 50, exceeded: false });
    expect(deltaCheck(6.1, 4, 50)).toEqual({ percent: 52.5, exceeded: true });
    expect(deltaCheck(2, 4, 50)).toEqual({ percent: 50, exceeded: false });
  });

  it('flags any change away from zero and can be disabled', () => {
    expect(deltaCheck(1, 0, 50)).toEqual({ percent: null, exceeded: true });
    expect(deltaCheck(0, 0, 50)).toEqual({ percent: 0, exceeded: false });
    expect(deltaCheck(100, 1, 0)).toEqual({ percent: 9900, exceeded: false });
    expect(deltaCheck(5, null, 50)).toEqual({ percent: null, exceeded: false });
  });

  it('describes the change', () => {
    expect(formatDelta(6, 4, 50)).toBe('▲ 50%');
    expect(formatDelta(2, 4, 50)).toBe('▼ 50%');
    expect(formatDelta(1, 0, null)).toBe('▲ from 0');
    expect(formatDelta(null, 4, null)).toBeNull();
  });
});

describe('line interpretation while typing', () => {
  const context = { gender: 'Male' as const, ageDays: 40 * 365, ranges: [potassium] };

  it('keeps the server answer for unchanged values', () => {
    const saved = line({ status: 'Entered', value: '5.8', flag: 'High', deltaPercent: 20, deltaExceeded: false });
    expect(interpretLine(saved, { value: '5.8' }, context)).toMatchObject({
      flag: 'High',
      source: 'server',
      dirty: false,
    });
    expect(interpretLine(saved, undefined, context)).toMatchObject({ flag: 'High', source: 'server' });
  });

  it('previews flags, validity and delta for typed values', () => {
    const pending = line({ previousValue: 4 });
    expect(interpretLine(pending, { value: '6.9' }, context)).toMatchObject({
      flag: 'CriticalHigh',
      source: 'preview',
      deltaExceeded: true,
      dirty: true,
    });
    expect(interpretLine(pending, { value: '4.2' }, context)).toMatchObject({
      flag: 'Normal',
      deltaPercent: 5,
      deltaExceeded: false,
    });
    expect(interpretLine(pending, { value: 'high' }, context).invalid).toMatch(/number/);
    // Clearing a value that was never saved is not a change.
    expect(interpretLine(pending, { value: '' }, context)).toMatchObject({ flag: null, invalid: null, dirty: false });
    // Clearing a saved value is a change without a flag.
    expect(
      interpretLine(line({ status: 'Entered', value: '4.2', flag: 'Normal' }), { value: ' ' }, context),
    ).toMatchObject({ flag: null, dirty: true });
  });

  it('falls back to the printed interval when the test master is not available', () => {
    const pending = line({ referenceText: '3.5 - 5.1' });
    expect(interpretLine(pending, { value: '5.9' }, { gender: 'Male', ageDays: null })).toMatchObject({ flag: 'High' });
  });

  it('checks answers against the allowed options', () => {
    const answer = line({ resultType: 'Options', options: ['Negative', 'Positive'], referenceText: 'Negative' });
    expect(interpretLine(answer, { value: 'Maybe' }, { gender: 'Female', ageDays: 9000 }).invalid).toMatch(
      /Negative, Positive/,
    );
    const answerRanges = [range({ normalValues: ['Negative'], criticalValues: ['Positive'] })];
    expect(
      interpretLine(answer, { value: 'Positive' }, { gender: 'Female', ageDays: 9000, ranges: answerRanges }).flag,
    ).toBe('Critical');
  });
});

describe('verification hints', () => {
  it('lists the reasons to look at a result', () => {
    const entered = line({ status: 'Entered', value: '6.9', flag: 'CriticalHigh', isCritical: true });
    expect(reviewHints(entered, { flag: 'CriticalHigh', deltaExceeded: true, invalid: null })).toEqual([
      'Critical value: document the call to the doctor',
      'Large change from the previous result',
    ]);
    expect(reviewHints(line({ status: 'Entered' }), { flag: 'High', deltaExceeded: false, invalid: null })).toEqual([
      'Flagged high',
    ]);
    expect(reviewHints(line({ status: 'Entered' }), { flag: null, deltaExceeded: false, invalid: null })).toEqual([
      'No reference range applies to this patient',
    ]);
  });

  it('allows bulk verification only for clean, saved, normal results', () => {
    const clean = line({ status: 'Entered', value: '4.2', flag: 'Normal' });
    expect(isReadyForBulkVerify(clean, { flag: 'Normal', deltaExceeded: false, invalid: null, dirty: false })).toBe(
      true,
    );
    expect(isReadyForBulkVerify(clean, { flag: 'Normal', deltaExceeded: false, invalid: null, dirty: true })).toBe(
      false,
    );
    expect(
      isReadyForBulkVerify(line({ status: 'Entered', remarks: 'Repeat sample' }), {
        flag: 'Normal',
        deltaExceeded: false,
        invalid: null,
        dirty: false,
      }),
    ).toBe(false);
    expect(
      isReadyForBulkVerify(line({ status: 'Pending' }), {
        flag: 'Normal',
        deltaExceeded: false,
        invalid: null,
        dirty: false,
      }),
    ).toBe(false);
  });

  it('holds a normal value the analyser flagged, did not report as final, sent in another unit or commented on', () => {
    const normal = { flag: 'Normal' as const, deltaExceeded: false, invalid: null, dirty: false };
    const fromAnalyser = (partial: Partial<ResultSheetLine>) =>
      line({ status: 'Entered', value: '4.2', flag: 'Normal', instrumentCode: 'EM360', ...partial });

    expect(reviewHints(fromAnalyser({ instrumentFlags: 'W' }), normal)).toEqual(['Analyser flag W']);
    expect(reviewHints(fromAnalyser({ instrumentStatus: 'P' }), normal)).toEqual([
      'Not final on the analyser (status P)',
    ]);
    expect(reviewHints(fromAnalyser({ instrumentUnit: 'mg/dL' }), normal)).toEqual([
      'Analyser sent mg/dL, the test is in mmol/L',
    ]);
    expect(reviewHints(fromAnalyser({ instrumentComment: 'Haemolysed' }), normal)).toEqual(['Analyser comment']);
    for (const held of [
      { instrumentFlags: 'W' },
      { instrumentStatus: 'P' },
      { instrumentUnit: 'mg/dL' },
      { instrumentComment: 'Haemolysed' },
    ]) {
      expect(isReadyForBulkVerify(fromAnalyser(held), normal)).toBe(false);
    }

    // What the analyser says of a normal, final result in the test's unit (written its own way) is nothing to look at.
    const final = fromAnalyser({ instrumentFlags: 'N', instrumentStatus: 'F', instrumentUnit: 'mmol/l' });
    expect(reviewHints(final, normal)).toEqual([]);
    expect(isReadyForBulkVerify(final, normal)).toBe(true);
  });
});

describe('analyser readings', () => {
  it('reads flags, statuses and units as the server does', () => {
    expect(isBenignAnalyserFlag(null)).toBe(true);
    expect(isBenignAnalyserFlag(' n ')).toBe(true);
    expect(isBenignAnalyserFlag('H')).toBe(false);
    expect(isFinalOnAnalyser(null)).toBe(true);
    expect(isFinalOnAnalyser('f')).toBe(true);
    expect(isFinalOnAnalyser('C')).toBe(false);
    expect(isSameUnit('10*3/µL', '10*3/uL')).toBe(true);
    expect(isSameUnit('µIU/mL', 'μIU/mL')).toBe(true);
    expect(isSameUnit(null, 'g/dL')).toBe(true);
    expect(isSameUnit('%', null)).toBe(false);
    expect(isSameUnit('mmol/L', 'mg/dL')).toBe(false);
  });
});
