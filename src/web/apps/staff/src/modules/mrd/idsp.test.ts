import { describe, expect, it } from 'vitest';
import { addCaseProblems, reportProblems, weekLabel } from './idsp';

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
