import { describe, expect, it } from 'vitest';
import { judge, nextStep, zScore } from './eqas';

// External quality assessment rounds (NABL 112, ISO 15189): the z-score preview shown while the provider's evaluation is
// typed agrees with the server's (ISO 13528, two decimals), and each round offers its next step.

describe('EQAS rounds', () => {
  it('computes the z-score as the server does and bands it', () => {
    expect(zScore(1.62, 1.2, 0.1)).toBe(4.2);
    expect(zScore(90, 100, 4)).toBe(-2.5);
    expect(zScore(105, 100, 0)).toBeNull();
    expect(zScore(Number.NaN, 100, 2)).toBeNull();
    expect(judge(2)).toBe('Acceptable');
    expect(judge(-2.01)).toBe('Warning');
    expect(judge(3)).toBe('Warning');
    expect(judge(3.01)).toBe('Unacceptable');
  });

  it('offers the round its next step: result, evaluation, corrective action, acceptance', () => {
    const round = { reportedValue: null, performance: null, needsCapa: false, acceptedAt: null } as const;
    expect(nextStep(round)).toBe('report');
    expect(nextStep({ ...round, reportedValue: 1.6 })).toBe('evaluate');
    expect(nextStep({ ...round, reportedValue: 1.6, performance: 'Unacceptable', needsCapa: true })).toBe('capa');
    expect(nextStep({ ...round, reportedValue: 1.6, performance: 'Unacceptable' })).toBe('accept');
    expect(
      nextStep({ ...round, reportedValue: 1.6, performance: 'Acceptable', acceptedAt: '2026-11-08T05:00:00Z' }),
    ).toBe('done');
  });
});
