import { describe, expect, it } from 'vitest';
import type { QcStatusRow } from './api';
import { chartBounds, formatZ, outcomeColor, QC_COLORS, sdLines, sdLineStyle, sdZone, summarizeQc, zScore } from './qc';

describe('Levey-Jennings SD bands', () => {
  it('computes the mean and ±1/2/3 SD lines', () => {
    const lines = sdLines(100, 4);
    expect(lines.map((l) => l.label)).toEqual(['-3SD', '-2SD', '-1SD', 'Mean', '+1SD', '+2SD', '+3SD']);
    expect(lines.map((l) => l.value)).toEqual([88, 92, 96, 100, 104, 108, 112]);
  });

  it('keeps decimal targets exact', () => {
    expect(sdLines(5.2, 0.15).map((l) => l.value)).toEqual([4.75, 4.9, 5.05, 5.2, 5.35, 5.5, 5.65]);
  });

  it('computes z-scores and zones with strict limits', () => {
    expect(zScore(108, 100, 4)).toBe(2);
    expect(zScore(90, 100, 0)).toBe(0);
    expect(sdZone(0.5)).toBe('within1');
    expect(sdZone(-1)).toBe('within1');
    expect(sdZone(2)).toBe('within2');
    expect(sdZone(-2.01)).toBe('within3');
    expect(sdZone(3.2)).toBe('beyond3');
  });

  it('frames the chart at ±4 SD and keeps outliers visible', () => {
    expect(chartBounds(100, 4, [98, 103])).toEqual({ min: 84, max: 116 });
    expect(chartBounds(100, 4, [121])).toEqual({ min: 84, max: 123 });
    expect(chartBounds(100, 4, [70])).toEqual({ min: 68, max: 116 });
  });

  it('styles lines by distance from the mean', () => {
    expect(sdLineStyle(0)).toMatchObject({ type: 'solid', color: QC_COLORS.mean });
    expect(sdLineStyle(-1).type).toBe('dotted');
    expect(sdLineStyle(2).type).toBe('dashed');
    expect(sdLineStyle(-3)).toMatchObject({ type: 'solid', color: QC_COLORS.sd3 });
    expect(outcomeColor('Rejected')).toBe(QC_COLORS.rejected);
    expect(outcomeColor('AcceptedWithWarning')).toBe(QC_COLORS.warning);
  });

  it('formats z-scores for people', () => {
    expect(formatZ(2.345)).toBe('+2.35 SD');
    expect(formatZ(-1.1)).toBe('−1.10 SD');
    expect(formatZ(0)).toBe('0.00 SD');
  });
});

describe('QC board summary', () => {
  const row = (partial: Partial<QcStatusRow>): QcStatusRow => ({
    labTestId: 't1',
    testCode: 'GLU',
    testName: 'Glucose',
    lotId: 'l1',
    lotNumber: 'A1',
    level: 1,
    instrumentCode: 'EM360',
    latestOutcome: null,
    latestRunAt: null,
    locked: false,
    overridden: false,
    ...partial,
  });

  it('counts outcomes and lists locked assays once', () => {
    const summary = summarizeQc([
      row({ lotId: 'l1', latestOutcome: 'Accepted' }),
      row({ lotId: 'l2', level: 2, latestOutcome: 'Rejected', locked: true }),
      row({ lotId: 'l3', labTestId: 't2', testName: 'Creatinine', latestOutcome: 'AcceptedWithWarning' }),
      row({ lotId: 'l4', labTestId: 't3', testName: 'Potassium' }),
      row({ lotId: 'l5', level: 3, latestOutcome: 'Rejected', locked: true }),
    ]);
    expect(summary).toEqual({ lots: 5, accepted: 1, warnings: 1, rejected: 2, notRun: 1, lockedTests: ['Glucose'] });
  });
});
