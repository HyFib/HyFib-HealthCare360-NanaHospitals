import { describe, expect, it } from 'vitest';
import { cardiologyActions, isCardiologyReportable, isCardiologyReportEditable, nextCardiologyAction } from './workflow';

describe('cardiology workflow', () => {
  it('offers only allowed actions', () => {
    expect(cardiologyActions('Ordered')).toContain('complete');
    expect(cardiologyActions('InProgress')).not.toContain('start');
    expect(cardiologyActions('Reported')).toEqual(['attach']);
    expect(cardiologyActions('Released')).toEqual([]);
  });

  it('suggests start, then the tracing, then completion', () => {
    expect(nextCardiologyAction('Ordered', 0)).toBe('start');
    expect(nextCardiologyAction('InProgress', 0)).toBe('attach');
    expect(nextCardiologyAction('InProgress', 2)).toBe('complete');
    expect(nextCardiologyAction('Acquired', 2)).toBeNull();
  });

  it('knows when the report can be written and edited', () => {
    expect(isCardiologyReportable('InProgress')).toBe(false);
    expect(isCardiologyReportable('Acquired')).toBe(true);
    expect(isCardiologyReportEditable(null, 'Acquired')).toBe(true);
    expect(isCardiologyReportEditable('Reported', 'Reported')).toBe(true);
    expect(isCardiologyReportEditable('Verified', 'Verified')).toBe(false);
    expect(isCardiologyReportEditable(null, 'InProgress')).toBe(false);
  });
});
