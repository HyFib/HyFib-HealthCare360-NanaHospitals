import { describe, expect, it } from 'vitest';
import {
  addDays,
  daysBetween,
  firstName,
  formatCalendarDate,
  formatCountdown,
  formatDate,
  formatInr,
  formatNumber,
  formatTime,
  istDateOf,
  parseCalendarDate,
} from './format';

describe('dates in hospital time (IST)', () => {
  it('shows an instant on its IST day, whatever the device time zone', () => {
    // 20:00 UTC on 31 March is 01:30 on 1 April in India.
    expect(istDateOf('2026-03-31T20:00:00Z')).toBe('2026-04-01');
    expect(formatDate('2026-03-31T20:00:00Z', 'en')).toBe('1 Apr 2026');
  });

  it('shows clock times in IST with am/pm', () => {
    expect(formatTime('2026-04-01T04:30:00Z', 'en').toLowerCase().replace(/\s/g, ' ')).toBe('10:00 am');
  });

  it('never shifts a calendar date', () => {
    expect(formatCalendarDate('2026-12-31', 'en')).toBe('31 Dec 2026');
    expect(parseCalendarDate('2026-02-30')).toBeNull();
    expect(parseCalendarDate('not a date')).toBeNull();
  });

  it('shows a dash for missing or unreadable values', () => {
    expect(formatDate(null, 'en')).toBe('—');
    expect(formatDate('garbage', 'en')).toBe('—');
    expect(formatInr(undefined, 'en')).toBe('—');
    expect(formatNumber(Number.NaN, 'en')).toBe('—');
  });

  it('adds days and counts days between calendar dates', () => {
    expect(addDays('2026-02-27', 2)).toBe('2026-03-01');
    expect(addDays('2026-01-01', -1)).toBe('2025-12-31');
    expect(daysBetween('2026-03-01', '2026-03-08')).toBe(7);
    expect(daysBetween('2026-03-08', '2026-03-01')).toBe(-7);
  });
});

describe('money and numbers', () => {
  it('uses Indian digit grouping', () => {
    expect(formatInr(123456, 'en')).toBe('₹1,23,456.00');
    expect(formatInr(1234567.5, 'en', { whole: true })).toBe('₹12,34,568');
    expect(formatNumber(150000, 'en')).toBe('1,50,000');
  });

  it('keeps Latin digits in Hindi (as on bills and SMS)', () => {
    expect(formatInr(2500, 'hi')).toMatch(/2,500\.00/);
  });
});

describe('small helpers', () => {
  it('formats countdowns', () => {
    expect(formatCountdown(299_001)).toBe('05:00');
    expect(formatCountdown(3_900_000)).toBe('1:05:00');
    expect(formatCountdown(-5)).toBe('00:00');
  });

  it('picks a first name for greetings, skipping titles', () => {
    expect(firstName('Dr. Meera Iyer')).toBe('Meera');
    expect(firstName('Smt Lakshmi Devi')).toBe('Lakshmi');
    expect(firstName('Ravi')).toBe('Ravi');
    expect(firstName(null)).toBe('');
  });
});
