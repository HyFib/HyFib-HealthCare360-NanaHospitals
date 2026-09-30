import { describe, expect, it } from 'vitest';
import {
  capaActions,
  capaTone,
  formatIndicatorValue,
  indicatorValue,
  investigationGaps,
  isSentinel,
  meetsTarget,
  residualProblem,
  riskRating,
  riskScore,
  severityRank,
  transitionBlocker,
  transitionLabel,
  transitionNeedsNote,
} from './rules';

describe('incident rules', () => {
  it('ranks severity by harm, not alphabetically', () => {
    expect(severityRank('Death')).toBeGreaterThan(severityRank('Severe'));
    expect(severityRank('Moderate')).toBeGreaterThan(severityRank('Mild'));
    expect(severityRank('NearMiss')).toBe(0);
  });

  it('recognises sentinel events like the server', () => {
    expect(isSentinel('Death', null, false)).toBe(true);
    expect(isSentinel('Severe', null, false)).toBe(true);
    expect(isSentinel('NoHarm', 'WrongPatient', false)).toBe(true);
    expect(isSentinel('Mild', null, true)).toBe(true);
    expect(isSentinel('Moderate', null, false)).toBe(false);
  });

  it('asks for notes when closing and reopening', () => {
    expect(transitionNeedsNote('Investigation', 'Closed')).toBe(true);
    expect(transitionNeedsNote('Closed', 'UnderReview')).toBe(true);
    expect(transitionNeedsNote('Reported', 'UnderReview')).toBe(false);
    expect(transitionLabel('Closed', 'UnderReview')).toBe('Reopen');
    expect(transitionLabel('Reported', 'Closed')).toBe('Close (duplicate or invalid)');
  });

  it('blocks moves the workflow refuses', () => {
    const base = { isSentinel: false, rcaCompleted: false, capaCount: 0, openCapaCount: 0 };
    expect(transitionBlocker('Reported', 'Investigation', base)).toBe('This move is not allowed.');
    expect(transitionBlocker('Investigation', 'Closed', base)).toBe('Complete the root-cause analysis first.');
    expect(transitionBlocker('UnderReview', 'Closed', { ...base, isSentinel: true })).toBe(
      'A sentinel event can be closed only after a completed root-cause analysis.',
    );
    expect(transitionBlocker('Reported', 'Closed', { ...base, isSentinel: true })).toBeNull();
    expect(transitionBlocker('UnderReview', 'CapaInProgress', base)).toBe(
      'Add at least one corrective or preventive action first.',
    );
    expect(
      transitionBlocker('CapaInProgress', 'Closed', { ...base, rcaCompleted: true, capaCount: 2, openCapaCount: 1 }),
    ).toBe('1 action(s) are still open.');
    expect(transitionBlocker('UnderReview', 'Investigation', base)).toBeNull();
  });

  it('lists what the analysis still needs', () => {
    const empty = { method: 'FiveWhys' as const, problemStatement: '', findings: '', whys: ['a'], factors: [] };
    expect(investigationGaps(empty)).toHaveLength(3);
    expect(
      investigationGaps({
        method: 'Fishbone',
        problemStatement: 'Wrong drug given',
        findings: 'Look-alike packaging',
        whys: [],
        factors: [
          { description: 'Similar packaging', isRootCause: true },
          { description: 'Night shift staffing', isRootCause: false },
        ],
      }),
    ).toEqual([]);
  });
});

describe('CAPA rules', () => {
  it('lets the owner work an action and the quality team verify it', () => {
    expect(capaActions('Open', { isOwner: true, isManager: false })).toEqual(['start', 'complete']);
    expect(capaActions('InProgress', { isOwner: false, isManager: true })).toEqual(['complete', 'edit', 'cancel']);
    expect(capaActions('Completed', { isOwner: false, isManager: true })).toEqual(['verify']);
    expect(capaActions('Completed', { isOwner: true, isManager: true })).toEqual([]);
    expect(capaActions('Verified', { isOwner: false, isManager: true })).toEqual([]);
  });

  it('shows overdue actions in red', () => {
    expect(capaTone('InProgress', true)).toBe('danger');
    expect(capaTone('Verified', false)).toBe('success');
  });
});

describe('risk matrix', () => {
  it('scores and rates like the server', () => {
    expect(riskScore(4, 5)).toBe(20);
    expect(riskRating(4)).toBe('Low');
    expect(riskRating(5)).toBe('Medium');
    expect(riskRating(9)).toBe('Medium');
    expect(riskRating(10)).toBe('High');
    expect(riskRating(16)).toBe('High');
    expect(riskRating(20)).toBe('Extreme');
  });

  it('checks residual risk', () => {
    expect(residualProblem(3, 3, 2, null)).toMatch(/both likelihood and impact/);
    expect(residualProblem(2, 2, 3, 3)).toMatch(/cannot be higher/);
    expect(residualProblem(4, 4, 2, 3)).toBeNull();
    expect(residualProblem(4, 4, null, null)).toBeNull();
  });
});

describe('quality indicators', () => {
  it('computes values with the unit multiplier', () => {
    expect(indicatorValue(45, 50, 'Percent')).toBe(90);
    expect(indicatorValue(3, 1234, 'PerThousand')).toBe(2.43);
    expect(indicatorValue(120, 40, 'Minutes')).toBe(3);
    expect(indicatorValue(1, 3, 'Ratio', 10)).toBe(3.33);
    expect(indicatorValue(1, 0, 'Percent')).toBeNull();
  });

  it('judges the target by direction', () => {
    expect(meetsTarget(90, 85, 'HigherIsBetter')).toBe(true);
    expect(meetsTarget(90, 85, 'LowerIsBetter')).toBe(false);
    expect(meetsTarget(90, null, 'LowerIsBetter')).toBeNull();
  });

  it('formats values with their unit', () => {
    expect(formatIndicatorValue(92.5, 'Percent')).toBe('92.5%');
    expect(formatIndicatorValue(3.2, 'PerThousand')).toBe('3.2 per 1,000');
    expect(formatIndicatorValue(1, 'Days')).toBe('1 day');
    expect(formatIndicatorValue(null, 'Count')).toBe('—');
  });
});
