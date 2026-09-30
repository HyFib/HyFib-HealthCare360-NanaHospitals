import { describe, expect, it } from 'vitest';
import {
  canSettle,
  isValidEmployeeCode,
  isValidHsnSac,
  isValidPeriod,
  monthLabel,
  normalizeEmployeeCode,
  recentMonths,
} from './rules';

describe('HSN/SAC codes (mirror of the server)', () => {
  it.each([
    ['2201', true],
    ['220190', true],
    ['22019090', true],
    [' 996333 ', true],
    ['', false],
    ['22019', false],
    ['2201909', false],
    ['220190901', false],
    ['99633A', false],
    ['22 0190', false],
    ['２２０１', false],
  ])('%s is valid: %s', (code, valid) => {
    expect(isValidHsnSac(code)).toBe(valid);
  });
});

describe('staff credit rules (mirror of the server)', () => {
  it.each([
    ['EMP123', true],
    ['nh/2021-45', true],
    [' E1 ', true],
    ['E', false],
    ['-E12', false],
    ['E 12', false],
    ['A'.repeat(21), false],
  ])('employee code %s is valid: %s', (code, valid) => {
    expect(isValidEmployeeCode(code)).toBe(valid);
  });

  it('normalises codes like the server', () => {
    expect(normalizeEmployeeCode(' nh-0045 ')).toBe('NH-0045');
  });

  it('settles only months that have ended', () => {
    expect(isValidPeriod('2026-09')).toBe(true);
    expect(isValidPeriod('2026-13')).toBe(false);
    expect(canSettle('2026-08', '2026-09')).toBe(true);
    expect(canSettle('2026-09', '2026-09')).toBe(false);
    expect(canSettle('2026-10', '2026-09')).toBe(false);
  });
});

describe('payroll months', () => {
  it('lists recent months newest first, across a year end', () => {
    expect(recentMonths('2026-02', 4)).toEqual(['2026-02', '2026-01', '2025-12', '2025-11']);
  });

  it('names a month in words', () => {
    expect(monthLabel('2026-09')).toBe('September 2026');
    expect(monthLabel('bad')).toBe('bad');
  });
});
