import { describe, expect, it } from 'vitest';
import { addCaseProblems, caseFirstDay, daysWaiting, nikshayProblems, reportProblems, weekLabel } from './idsp';

describe('IDSP week report', () => {
  const sunday = '2026-10-25';

  it('is sent after the week ends and not later than today', () => {
    expect(reportProblems('2026-10-26', sunday, '2026-10-27')).toBeNull();
    expect(reportProblems('2026-10-25', sunday, '2026-10-27')).toBe('The form is sent after the week ends.');
    expect(reportProblems('2026-10-28', sunday, '2026-10-27')).toBe('The date cannot be in the future.');
    expect(reportProblems('', sunday, '2026-10-27')).toBe('Give the date the form was sent.');
  });

  it('names the week as IHIP does', () => {
    expect(weekLabel(2026, 3)).toBe('2026-W03');
    expect(weekLabel(2026, 43)).toBe('2026-W43');
  });
});

describe('a case added by hand', () => {
  const blank = {
    patientId: 'p1',
    diseaseCode: 'AFP',
    onsetDate: '',
    testName: '',
    testResult: '',
    today: '2026-10-25',
  };

  it('needs a patient and a disease, and nothing else for a presumptive case', () => {
    expect(addCaseProblems(blank)).toEqual({ patient: null, disease: null, onset: null, test: null });
    expect(addCaseProblems({ ...blank, patientId: null, diseaseCode: '' })).toMatchObject({
      patient: 'Choose the patient.',
      disease: 'Choose the disease.',
    });
  });

  it('names the test when a result is given, and has no onset in the future', () => {
    expect(addCaseProblems({ ...blank, testResult: 'Positive' }).test).toBe('Name the test that confirmed it.');
    expect(addCaseProblems({ ...blank, testResult: 'Positive', testName: 'Dengue IgM' }).test).toBeNull();
    expect(addCaseProblems({ ...blank, onsetDate: '2026-10-26' }).onset).toBe('The onset cannot be in the future.');
  });
});

describe('a tuberculosis case notified on Ni-kshay', () => {
  // Diagnosed at 00:30 on 19 October in India: still 18 October in UTC.
  const tb = { presumedAt: '2026-10-18T19:00:00Z', confirmedAt: null };

  it('starts on the day it was diagnosed in India', () => {
    expect(caseFirstDay(tb)).toBe('2026-10-19');
    expect(caseFirstDay({ presumedAt: null, confirmedAt: '2026-10-20T06:00:00Z' })).toBe('2026-10-20');
    expect(caseFirstDay({ presumedAt: null, confirmedAt: null })).toBeNull();
    expect(daysWaiting(tb, '2026-10-28')).toBe(9);
    expect(daysWaiting(tb, '2026-10-19')).toBe(0);
  });

  it('is recorded with the ID Ni-kshay gave, on a day from its diagnosis to today', () => {
    expect(nikshayProblems('10234567', '2026-10-27', '2026-10-19', '2026-10-28')).toEqual({ id: null, date: null });
    expect(nikshayProblems(' ', '2026-10-27', '2026-10-19', '2026-10-28').id).toBe('Enter the Ni-kshay ID.');
    expect(nikshayProblems('102-345', '2026-10-27', '2026-10-19', '2026-10-28').id).toBe(
      'Enter the ID as Ni-kshay gave it: letters and digits.',
    );
    expect(nikshayProblems('10234567', '', '2026-10-19', '2026-10-28').date).toBe('Give the day it was notified.');
    expect(nikshayProblems('10234567', '2026-10-29', '2026-10-19', '2026-10-28').date).toBe(
      'The date cannot be in the future.',
    );
    expect(nikshayProblems('10234567', '2026-10-18', '2026-10-19', '2026-10-28').date).toBe(
      'It cannot be notified before it was diagnosed.',
    );
  });
});
