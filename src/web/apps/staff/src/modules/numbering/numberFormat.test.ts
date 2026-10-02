import { describe, expect, it } from 'vitest';
import {
  checkChange,
  formatChecks,
  formatProblems,
  hospitalDate,
  insertToken,
  largestStartAt,
  lastIssuedText,
  longestNumber,
  nextRestart,
  outline,
  parseFormat,
  renderNumber,
  resetProblem,
  restartSentence,
  type FormatChange,
} from './numberFormat';

// The same cases as the server's NumberFormatTests and NumberFormatRulesTests: the editor must say what the server says.

const october2 = { year: 2026, month: 10, day: 2 };

function change(overrides: Partial<FormatChange> = {}): FormatChange {
  return {
    name: 'OP bill',
    perFacility: true,
    isLocked: false,
    maxLength: 24,
    defaultFormat: '{FAC}OB{YY}{SEQ:7}',
    format: '{FAC}OB{YY}{SEQ:7}',
    resetPolicy: 'Yearly',
    startAt: null,
    currentFormat: '{FAC}OB{YY}{SEQ:7}',
    currentResetPolicy: 'Yearly',
    lastNumber: 0,
    facilityCount: 1,
    longestFacilityCode: 6,
    otherFormats: [],
    ...overrides,
  };
}

const employee = (overrides: Partial<FormatChange> = {}) =>
  change({
    name: 'Employee code',
    maxLength: 20,
    defaultFormat: '{FAC}EMP{SEQ:5}',
    format: '{FAC}EMP{SEQ:5}',
    currentFormat: '{FAC}EMP{SEQ:5}',
    resetPolicy: 'Never',
    currentResetPolicy: 'Never',
    ...overrides,
  });

describe('renderNumber', () => {
  it.each([
    ['{FAC}OB{YY}{SEQ:7}', 123, 'NHOB260000123'],
    ['{FAC}/OP/{YYYY}/{SEQ:5}', 42, 'NH/OP/2026/00042'],
    ['VP{YY}{MM}{DD}-{SEQ:4}', 7, 'VP261002-0007'],
    ['{SEQ:9}', 1, '000000001'],
    ['{FAC}{SEQ}', 1234567, 'NH1234567'],
  ])('makes %s into the number', (format, value, expected) => {
    expect(renderNumber(format, value, 'NH', october2)).toBe(expected);
  });

  it('lets the running number outgrow its padding and writes unknown parts as they are', () => {
    expect(renderNumber('{FAC}AS{SEQ:3}', 12345, 'NH', october2)).toBe('NHAS12345');
    expect(renderNumber('{FAC}-{XYZ}-{SEQ:2}-{YY', 5, 'NH', october2)).toBe('NH-{XYZ}-05-{YY');
    expect(renderNumber('{FAC}DN{SEQ:6}', 9, null, october2)).toBe('DN000009');
  });

  it('caps a padding the rules refuse rather than building it', () => {
    // Saved before the rules existed: padding 999999999 digits would crash the page.
    expect(renderNumber('{FAC}AS{SEQ:999999999}', 42, 'NH', october2)).toBe('NHAS000000000042');
    expect(renderNumber('{FAC}AS{SEQ:13}', 42, 'NH', october2)).toBe('NHAS000000000042');
  });

  it('splits a format into its parts', () => {
    expect(parseFormat('{FAC}/OP{YY}{SEQ:6}{seq}{SEQ:x}{SEQ:123}').map((p) => p.kind)).toEqual([
      'facilityCode',
      'text',
      'shortYear',
      'sequence',
      'unknown',
      'unknown',
      'sequence',
    ]);
    expect(parseFormat('AB{SEQ')).toEqual([
      { kind: 'text', text: 'AB' },
      { kind: 'unclosed', text: '{SEQ' },
    ]);
  });
});

describe('dates', () => {
  it('uses the date in India', () => {
    // 20:00 UTC on 1 October is 01:30 on 2 October in India.
    expect(hospitalDate(new Date('2026-10-01T20:00:00Z'))).toEqual(october2);
  });

  it('knows when numbers next start again', () => {
    expect(nextRestart('Never', october2)).toBeNull();
    expect(nextRestart('Yearly', october2)).toEqual({ year: 2027, month: 1, day: 1 });
    expect(nextRestart('Monthly', { year: 2026, month: 12, day: 31 })).toEqual({ year: 2027, month: 1, day: 1 });
    expect(nextRestart('Daily', { year: 2026, month: 2, day: 28 })).toEqual({ year: 2026, month: 3, day: 1 });
  });
});

describe('formatProblems', () => {
  it.each(['{FAC}OB{YY}{SEQ:7}', '{FAC}/OP/{YYYY}/{SEQ:6}', 'VP{YY}{MM}{DD}-{SEQ:4}', '{SEQ}', '{SEQ:12}'])(
    'accepts %s',
    (format) => {
      expect(formatProblems(format)).toEqual([]);
    },
  );

  it.each([
    ['', 'Enter a format, for example {FAC}{YY}{SEQ:6}.'],
    ['   ', 'Enter a format, for example {FAC}{YY}{SEQ:6}.'],
    ['{FAC}OB{YY}', 'Add {SEQ} or {SEQ:n} for the running number.'],
    ['{SEQ:4}-{SEQ:4}', 'Use the running number {SEQ} only once.'],
    ['{FAC}{SEQ:0}', 'In {SEQ:n}, n is the number of digits and must be from 1 to 12.'],
    ['{FAC}{SEQ:13}', 'In {SEQ:n}, n is the number of digits and must be from 1 to 12.'],
    ['{FAC}{YEAR}{SEQ}', '{YEAR} is not a part a number can have. Use {FAC}, {YYYY}, {YY}, {MM}, {DD} and {SEQ:n}.'],
    [
      '{FY}{YR}{SEQ}',
      '{FY} and {YR} are not parts a number can have. Use {FAC}, {YYYY}, {YY}, {MM}, {DD} and {SEQ:n}.',
    ],
    ['{FAC}OB{YY{SEQ}', '{YY{SEQ} is not a part a number can have. Use {FAC}, {YYYY}, {YY}, {MM}, {DD} and {SEQ:n}.'],
    ['OB{SEQ', 'A { has no closing }. Parts in braces look like {YY} or {SEQ:6}.'],
    [
      'op {SEQ}',
      'Outside the braces use only capital letters A to Z, digits, - and /. Not allowed: small letters and spaces.',
    ],
    ['OP.{SEQ}_', 'Outside the braces use only capital letters A to Z, digits, - and /. Not allowed: "." and "_".'],
  ])('explains what is wrong with "%s"', (format, message) => {
    expect(formatProblems(format)[0]).toBe(message);
  });

  it('refuses a format longer than the database keeps', () => {
    expect(formatProblems('{SEQ}' + 'A'.repeat(60))).toContain('Use at most 64 characters.');
  });
});

describe('resetProblem', () => {
  it.each([
    ['{FAC}{SEQ:6}', 'Never', true],
    ['{FAC}{YY}{SEQ:6}', 'Yearly', true],
    ['{FAC}{YYYY}{SEQ:6}', 'Yearly', true],
    ['{FAC}{SEQ:6}', 'Yearly', false],
    ['{FAC}{YY}{MM}{SEQ:4}', 'Monthly', true],
    ['{FAC}{MM}{SEQ:4}', 'Monthly', false],
    ['{FAC}{YY}{SEQ:4}', 'Monthly', false],
    ['VP{YY}{MM}{DD}-{SEQ:4}', 'Daily', true],
    ['VP{YY}{MM}-{SEQ:4}', 'Daily', false],
  ] as const)('%s restarting %s is allowed: %s', (format, policy, allowed) => {
    expect(resetProblem(format, policy) === null).toBe(allowed);
  });
});

describe('longestNumber and largestStartAt', () => {
  it('counts the longest facility code and at least eight digits of running number', () => {
    expect(longestNumber('{FAC}/OP/{YYYY}/{SEQ:6}', 6)).toEqual({
      facilityCode: 6,
      date: 4,
      runningNumber: 8,
      fixedText: 5,
      total: 23,
    });
    expect(longestNumber('{SEQ:9}', 6).total).toBe(9);
  });

  it('follows the running number', () => {
    expect(largestStartAt(false, '{FAC}OB{YY}{SEQ:4}')).toBe(99_999_999);
    expect(largestStartAt(false, '{FAC}OB{YY}{SEQ:10}')).toBe(9_999_999_999);
    expect(largestStartAt(true, '{FAC}{YY}{SEQ:6}')).toBe(999_999);
    expect(largestStartAt(false, '{FAC}OB{YY}')).toBeNull();
  });
});

describe('checkChange', () => {
  it('has no errors for a good change', () => {
    expect(checkChange(change({ format: '{FAC}/OP/{YY}/{SEQ:6}' }))).toEqual({});
  });

  it('says what to shorten when numbers would not fit', () => {
    expect(checkChange(employee({ format: '{FAC}EMPLOYEE{YYYY}{SEQ:5}' }))).toEqual({
      format: [
        'Numbers in this format can be up to 26 characters long (facility code 6, date 4, running number 8, fixed text 8), and only 20 fit. ' +
          'Shorten the fixed text or use {YY} instead of {YYYY}.',
      ],
    });
    expect(checkChange(employee({ format: '{FAC}EMPLOYEE{SEQ:5}', longestFacilityCode: 4 }))).toEqual({});
  });

  it('asks for the facility code once there are two facilities', () => {
    expect(checkChange(change({ format: 'OB{YY}{SEQ:7}' }))).toEqual({});
    expect(checkChange(change({ format: 'OB{YY}{SEQ:7}', facilityCount: 2 })).format).toEqual([
      'Add {FAC}. Each facility counts its own numbers, so without the facility code two facilities would issue the same numbers.',
    ]);
    expect(checkChange(change({ format: 'BR{YY}{SEQ:4}', perFacility: false, facilityCount: 2 }))).toEqual({});
  });

  it('keeps a changed format apart from the other series, but lets a default be restored', () => {
    const ipBill = { name: 'IP bill', format: '{FAC}IB{YY}{SEQ:7}', isDefault: true };
    expect(checkChange(change({ format: '{FAC}IB{YY}{SEQ:7}', otherFormats: [ipBill] })).format).toEqual([
      'IP bill already uses this format. Change the fixed text so the two kinds of number can be told apart.',
    ]);

    const restore = change({
      name: 'Refund',
      defaultFormat: '{FAC}RF{YY}{SEQ:6}',
      format: '{FAC}RF{YY}{SEQ:6}',
      currentFormat: '{FAC}/RF/{YY}/{SEQ:6}',
      otherFormats: [{ name: 'Refill request', format: '{FAC}RF{YY}{SEQ:6}', isDefault: true }],
    });
    expect(checkChange(restore)).toEqual({});
    expect(
      checkChange({
        ...restore,
        otherFormats: [{ name: 'Credit note', format: '{FAC}RF{YY}{SEQ:6}', isDefault: false }],
      }),
    ).toHaveProperty('format');
  });

  it.each([
    ['{FAC}OB{YY}{SEQ:7}', '{FAC}OB#'],
    ['{FAC}OB{SEQ:9}', '{FAC}OB#'],
    ['{FAC}OB26{SEQ}', '{FAC}OB#'],
    ['{FAC}/OP/{YYYY}/{SEQ:6}', '{FAC}/OP/#/#'],
    ['B{YY}X{SEQ}', 'B#X#'],
  ])('reads %s with each run of digits as one place', (format, expected) => {
    expect(outline(format)).toBe(expected);
  });

  // {SEQ:1} and {SEQ}, or {SEQ:06} and {SEQ:6}, make the same numbers; {SEQ:6} reaching 10000000 makes {SEQ:8}'s.
  it.each([
    ['{FAC}OB{YY}{SEQ:1}', '{FAC}OB{YY}{SEQ}'],
    ['{FAC}OB{YY}{SEQ:06}', '{FAC}OB{YY}{SEQ:6}'],
    ['{FAC}OB{YY}{SEQ:8}', '{FAC}OB{YY}{SEQ:6}'],
    ['{FAC}OB{YY}{SEQ:4}', '{FAC}OB{YY}0{SEQ:3}'],
    ['{FAC}OB{YY}{SEQ:6}', '{FAC}OB26{SEQ:6}'],
  ])('refuses %s while another series uses %s', (format, other) => {
    expect(
      checkChange(change({ format, otherFormats: [{ name: 'IP bill', format: other, isDefault: false }] })).format,
    ).toEqual([
      `IP bill uses ${other}, which can make the same numbers: the two differ only in digits, dates or the length of ` +
        'the running number. Change the letters, - or / so the two kinds of number can be told apart.',
    ]);
  });

  it.each([
    ['{FAC}AB{YY}{SEQ:7}', 'AB', 'ABAB#'],
    ['AB{FAC}{YY}{SEQ:7}', 'AB', 'ABAB#'],
    ['{FAC}{SEQ:6}', 'NH1', 'NH#'],
    ['{FAC}OB{SEQ}', null, '{FAC}OB#'],
  ])('reads %s at facility %s with the code in place of {FAC}', (format, code, expected) => {
    expect(outline(format, code)).toBe(expected);
  });

  it('refuses formats that make the same numbers once the facility code is filled in', () => {
    // At facility AB both make ABAB260000001: {FAC} is the facility's code, not a part apart.
    expect(
      checkChange(
        change({
          format: '{FAC}AB{YY}{SEQ:7}',
          facilityCode: 'AB',
          otherFormats: [{ name: 'IP bill', format: 'AB{FAC}{YY}{SEQ:7}', isDefault: false, facilityCode: 'AB' }],
        }),
      ).format,
    ).toEqual([
      'IP bill uses AB{FAC}{YY}{SEQ:7}. With the facility code AB in place of {FAC}, the two can make the same numbers. ' +
        'Change the letters, - or / so the two kinds of number can be told apart.',
    ]);
    expect(
      checkChange(
        change({
          format: '{FAC}-{YY}{SEQ:7}',
          facilityCode: 'NH',
          otherFormats: [{ name: 'Visitor pass', format: 'NH-{YY}{SEQ:7}', isDefault: false, facilityCode: 'NH' }],
        }),
      ).format,
    ).toEqual([
      'Visitor pass uses NH-{YY}{SEQ:7}. With the facility code NH in place of {FAC}, the two can make the same numbers. ' +
        'Change the letters, - or / so the two kinds of number can be told apart.',
    ]);
  });

  it("compares formats at two facilities with each one's code", () => {
    expect(
      checkChange(
        change({
          format: '{FAC}C{YY}{SEQ:7}',
          facilityCode: 'AB',
          otherFormats: [{ name: 'IP bill', format: '{FAC}{YY}{SEQ:7}', isDefault: false, facilityCode: 'ABC' }],
        }),
      ).format,
    ).toEqual([
      'IP bill uses {FAC}{YY}{SEQ:7}. With the facility codes AB and ABC in place of {FAC}, the two can make the same ' +
        'numbers. Change the letters, - or / so the two kinds of number can be told apart.',
    ]);
    expect(
      checkChange(
        change({
          format: '{FAC}IB{YY}{SEQ:7}',
          facilityCode: 'NH',
          otherFormats: [{ name: 'IP bill', format: '{FAC}IB{YY}{SEQ:7}', isDefault: false, facilityCode: 'KH' }],
        }),
      ),
    ).toEqual({});
  });

  it.each([
    ['{FAC}/OP/{YY}/{SEQ:5}', '{FAC}/IP/{YY}/{SEQ:5}'],
    ['{FAC}OB{YYYY}{SEQ:6}', '{FAC}OB-{YY}{SEQ:7}'],
    ['{FAC}B{YY}X{SEQ:5}', '{FAC}BX{YY}{SEQ:5}'],
  ])('lets %s be used beside %s, whose letters differ', (format, other) => {
    expect(
      checkChange(change({ format, otherFormats: [{ name: 'IP bill', format: other, isDefault: false }] })),
    ).toEqual({});
  });

  it('checks only the next number when the format and reset stay as they are', () => {
    const visitorPass = change({
      name: 'Visitor pass',
      defaultFormat: 'VP{YY}{MM}{DD}-{SEQ:4}',
      format: 'VP{YY}{MM}{DD}-{SEQ:4}',
      currentFormat: 'VP{YY}{MM}{DD}-{SEQ:4}',
      resetPolicy: 'Daily',
      currentResetPolicy: 'Daily',
      startAt: 50,
      facilityCount: 2,
    });
    expect(checkChange(visitorPass)).toEqual({});
    expect(checkChange({ ...visitorPass, resetPolicy: 'Monthly' })).toHaveProperty('format');
  });

  it('asks for the date parts a new reset needs', () => {
    expect(checkChange(employee({ resetPolicy: 'Yearly' }))).toEqual({
      resetPolicy: [
        'Numbers that start again every year must contain the year ({YY} or {YYYY}), or numbers from earlier years would be issued again.',
      ],
    });
  });

  it.each([
    ['Yearly', 'Number 120 has already been issued this year. Enter a number above 120.'],
    ['Monthly', 'Number 120 has already been issued this month. Enter a number above 120.'],
    ['Daily', 'Number 120 has already been issued today. Enter a number above 120.'],
    ['Never', 'Number 120 has already been issued. Enter a number above 120.'],
  ] as const)('keeps the next number above the last one issued (%s)', (policy, message) => {
    const format = '{FAC}OB{YY}{MM}{DD}{SEQ:7}';
    const next = change({
      format,
      currentFormat: format,
      resetPolicy: policy,
      currentResetPolicy: policy,
      lastNumber: 120,
    });
    expect(checkChange({ ...next, startAt: 120 })).toEqual({ startAt: [message] });
    expect(checkChange({ ...next, startAt: 121 })).toEqual({});
  });

  it('keeps the next number within the digits of the running number', () => {
    expect(checkChange(change({ startAt: 99_999_999 }))).toEqual({});
    expect(checkChange(change({ startAt: 100_000_000 }))).toEqual({
      startAt: ['The next number can have at most 8 digits.'],
    });
    expect(checkChange(change({ startAt: 0 }))).toEqual({ startAt: ['Enter a number of 1 or more.'] });

    const uhid = change({
      name: 'UHID (patient number)',
      isLocked: true,
      perFacility: false,
      maxLength: 20,
      defaultFormat: '{FAC}{YY}{SEQ:6}',
      format: '{FAC}{YY}{SEQ:6}',
      currentFormat: '{FAC}{YY}{SEQ:6}',
      resetPolicy: 'Never',
      currentResetPolicy: 'Never',
      lastNumber: 4_000,
    });
    expect(checkChange({ ...uhid, startAt: 1_000_000 })).toEqual({
      startAt: ['The next number can be at most 999999, so that these numbers keep the shape other systems read.'],
    });
    expect(checkChange({ ...uhid, startAt: 250_001 })).toEqual({});
  });
});

describe('formatChecks', () => {
  it('lists the rules a good format passes', () => {
    expect(formatChecks(change({ facilityCount: 2 }))).toEqual([
      { ok: true, text: 'Contains the year, so numbers stay unique when they start again each year.' },
      { ok: true, text: 'Contains the facility code, so no two facilities issue the same number.' },
      { ok: true, text: 'Numbers are at most 18 characters long, and there is room for 24.' },
      { ok: true, text: 'Different from the other number formats.' },
    ]);
  });

  it('shows what is wrong in the words of the server', () => {
    expect(formatChecks(change({ format: '{FAC}OB{SEQ:7}' }))[0]).toEqual({
      ok: false,
      text: 'Numbers that start again every year must contain the year ({YY} or {YYYY}), or numbers from earlier years would be issued again.',
    });
    expect(formatChecks(change({ format: 'ob' }))).toEqual([
      {
        ok: false,
        text: 'Outside the braces use only capital letters A to Z, digits, - and /. Not allowed: small letters.',
      },
      { ok: false, text: 'Add {SEQ} or {SEQ:n} for the running number.' },
    ]);
  });
});

describe('insertToken', () => {
  it('puts the part where the cursor is, or over the selection', () => {
    expect(insertToken('OB{SEQ:7}', 0, 0, '{FAC}')).toEqual({
      value: '{FAC}OB{SEQ:7}',
      selectionStart: 5,
      selectionEnd: 5,
    });
    expect(insertToken('{FAC}OBXX{SEQ:7}', 7, 9, '{YY}')).toEqual({
      value: '{FAC}OB{YY}{SEQ:7}',
      selectionStart: 11,
      selectionEnd: 11,
    });
  });

  it('selects the digits of {SEQ:n} so the next key sets them', () => {
    expect(insertToken('{FAC}OB', 7, 7, '{SEQ:6}')).toEqual({
      value: '{FAC}OB{SEQ:6}',
      selectionStart: 12,
      selectionEnd: 13,
    });
  });
});

describe('restartSentence', () => {
  it('says when numbers start again', () => {
    expect(restartSentence('Yearly')).toBe('starts again every year');
    expect(restartSentence('Never')).toBe('never starts again');
  });
});

describe('lastIssuedText', () => {
  it('says how many numbers the current period has', () => {
    expect(lastIssuedText(123, 'Yearly')).toBe('123 this year');
    expect(lastIssuedText(0, 'Daily')).toBe('None today');
    expect(lastIssuedText(4512, 'Never')).toBe('4512');
    expect(lastIssuedText(0, 'Never')).toBe('None yet');
  });
});
