import { describe, expect, it } from 'vitest';
import {
  bazettQtc,
  dippingPattern,
  formatDuration,
  fridericiaQtc,
  heartRateHint,
  lvefCategory,
  parseDuration,
  predictedMaxHeartRate,
  qtcHint,
  targetHeartRatePercent,
} from './measurements';

describe('QT correction', () => {
  it('uses Bazett and Fridericia formulas', () => {
    expect(bazettQtc(400, 60)).toBe(400);
    expect(bazettQtc(360, 100)).toBe(465);
    expect(fridericiaQtc(360, 100)).toBe(427);
    expect(bazettQtc(null, 80)).toBeNull();
    expect(bazettQtc(400, 0)).toBeNull();
  });

  it('gives sex-specific QTc hints', () => {
    expect(qtcHint(455, 'Male')).toBe('Prolonged (above 450 ms)');
    expect(qtcHint(455, 'Female')).toBe('Within normal limits');
    expect(qtcHint(510, 'Female')).toBe('Markedly prolonged (above 500 ms)');
    expect(qtcHint(null, 'Male')).toBeNull();
  });

  it('describes the heart rate', () => {
    expect(heartRateHint(48)).toMatch(/Bradycardia/);
    expect(heartRateHint(72)).toBe('Normal rate');
    expect(heartRateHint(130)).toMatch(/Tachycardia/);
  });
});

describe('treadmill test', () => {
  it('computes the share of the age-predicted maximum heart rate', () => {
    expect(predictedMaxHeartRate(50)).toBe(170);
    expect(targetHeartRatePercent(153, 50)).toBe(90);
    expect(targetHeartRatePercent(153, null)).toBeNull();
    expect(predictedMaxHeartRate(0)).toBeNull();
  });

  it('reads and prints exercise duration', () => {
    expect(parseDuration('12:30')).toBe(750);
    expect(parseDuration('9')).toBe(540);
    expect(parseDuration('9:75')).toBeNull();
    expect(parseDuration('')).toBeNull();
    expect(formatDuration(750)).toBe('12:30');
    expect(formatDuration(65)).toBe('1:05');
    expect(formatDuration(null)).toBe('');
  });
});

describe('echo and ABPM', () => {
  it('categorises LVEF', () => {
    expect(lvefCategory(35)).toMatch(/^Reduced/);
    expect(lvefCategory(45)).toMatch(/^Mildly reduced/);
    expect(lvefCategory(60)).toMatch(/^Preserved/);
    expect(lvefCategory(null)).toBeNull();
  });

  it('names the nocturnal dipping pattern', () => {
    expect(dippingPattern(140, 120)).toEqual({ percent: 14.3, pattern: 'Dipper' });
    expect(dippingPattern(140, 130)).toEqual({ percent: 7.1, pattern: 'Non-dipper' });
    expect(dippingPattern(140, 145)).toEqual({ percent: -3.6, pattern: 'Reverse dipper' });
    expect(dippingPattern(140, 105)).toEqual({ percent: 25, pattern: 'Extreme dipper' });
    expect(dippingPattern(null, 120)).toBeNull();
  });
});
