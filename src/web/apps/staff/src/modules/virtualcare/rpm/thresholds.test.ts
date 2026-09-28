import { describe, expect, it } from 'vitest';
import { axisRange, describeLimits, evaluateSeverity, thresholdBands, validateLimits, type Limits } from './thresholds';

const systolic: Limits = { min: 90, max: 140, criticalMin: 80, criticalMax: 180 };
const spo2: Limits = { min: 94, max: null, criticalMin: 90, criticalMax: null };
const ecg: Limits = { min: null, max: 0, criticalMin: null, criticalMax: 1 };

describe('evaluateSeverity (same rules as the server)', () => {
  it('grades blood pressure at every boundary', () => {
    expect(evaluateSeverity(120, systolic)).toBe('Normal');
    expect(evaluateSeverity(90, systolic)).toBe('Normal');
    expect(evaluateSeverity(140, systolic)).toBe('Normal');
    expect(evaluateSeverity(141, systolic)).toBe('Warning');
    expect(evaluateSeverity(180, systolic)).toBe('Warning');
    expect(evaluateSeverity(181, systolic)).toBe('Critical');
    expect(evaluateSeverity(85, systolic)).toBe('Warning');
    expect(evaluateSeverity(79, systolic)).toBe('Critical');
  });

  it('handles one-sided limits and missing limits', () => {
    expect(evaluateSeverity(99, spo2)).toBe('Normal');
    expect(evaluateSeverity(92, spo2)).toBe('Warning');
    expect(evaluateSeverity(89, spo2)).toBe('Critical');
    expect(evaluateSeverity(2, ecg)).toBe('Critical');
    expect(evaluateSeverity(1, ecg)).toBe('Warning');
    expect(evaluateSeverity(0, ecg)).toBe('Normal');
    expect(evaluateSeverity(500, null)).toBe('Normal');
  });
});

describe('thresholdBands', () => {
  it('draws five bands for a two-sided range', () => {
    expect(thresholdBands(systolic, { min: 60, max: 200 })).toEqual([
      { from: 60, to: 80, severity: 'Critical' },
      { from: 80, to: 90, severity: 'Warning' },
      { from: 90, to: 140, severity: 'Normal' },
      { from: 140, to: 180, severity: 'Warning' },
      { from: 180, to: 200, severity: 'Critical' },
    ]);
  });

  it('draws only the lower bands when there is no upper limit', () => {
    expect(thresholdBands(spo2, { min: 85, max: 100 })).toEqual([
      { from: 85, to: 90, severity: 'Critical' },
      { from: 90, to: 94, severity: 'Warning' },
      { from: 94, to: 100, severity: 'Normal' },
    ]);
  });

  it('clips to the axis and drops empty bands', () => {
    expect(thresholdBands(systolic, { min: 100, max: 150 })).toEqual([
      { from: 100, to: 140, severity: 'Normal' },
      { from: 140, to: 150, severity: 'Warning' },
    ]);
    expect(thresholdBands(ecg, { min: -0.5, max: 2.5 })).toEqual([
      { from: -0.5, to: 0, severity: 'Normal' },
      { from: 0, to: 1, severity: 'Warning' },
      { from: 1, to: 2.5, severity: 'Critical' },
    ]);
  });
});

describe('axisRange', () => {
  it('fits readings and limits with padding, on readable steps', () => {
    expect(axisRange([120, 150], systolic, { min: 50, max: 300 })).toEqual({ min: 65, max: 195 });
  });

  it('stays within what is physically possible', () => {
    expect(axisRange([91, 97], spo2, { min: 50, max: 100 })).toEqual({ min: 88, max: 99 });
    expect(axisRange([98, 100], { min: 94, max: null, criticalMin: 90, criticalMax: null }, { min: 50, max: 100 }).max).toBe(100);
  });

  it('copes with no data and flat data', () => {
    expect(axisRange([], null, { min: 30, max: 45 })).toEqual({ min: 30, max: 45 });
    const flat = axisRange([72, 72], null, { min: 20, max: 300 });
    expect(flat.min).toBeLessThan(72);
    expect(flat.max).toBeGreaterThan(72);
  });
});

describe('validateLimits (same rules as the server)', () => {
  it('accepts normal limits', () => {
    expect(validateLimits(systolic)).toBeNull();
    expect(validateLimits(spo2)).toBeNull();
    expect(validateLimits(ecg)).toBeNull();
  });

  it('rejects empty, inverted and overlapping limits', () => {
    expect(validateLimits({ min: null, max: null, criticalMin: null, criticalMax: null })?.field).toBe('min');
    expect(validateLimits({ ...systolic, min: 150 })?.field).toBe('max');
    expect(validateLimits({ ...systolic, criticalMax: 130 })).toEqual({ field: 'criticalMax', message: 'Critical limits must lie outside the normal range.' });
    expect(validateLimits({ ...systolic, criticalMin: 95 })?.field).toBe('criticalMin');
  });
});

describe('describeLimits', () => {
  it('describes limits in plain words', () => {
    expect(describeLimits(systolic, 'mmHg')).toBe('90–140 mmHg (critical below 80 or above 180)');
    expect(describeLimits(spo2, '%')).toBe('94% or more (critical below 90)');
    expect(describeLimits(null, 'kg')).toBe('No limits set');
  });
});
