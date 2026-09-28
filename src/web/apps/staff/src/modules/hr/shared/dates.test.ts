import { describe, expect, it } from 'vitest';
import {
  addDays,
  addMonths,
  datesBetween,
  dayHeader,
  diffDays,
  dueLabel,
  formatMinutes,
  formatTimeOfDay,
  fromIstInput,
  fromTimeInput,
  isIsoDate,
  monthEnd,
  monthKeyLabel,
  monthLabel,
  startOfWeek,
  toIstInput,
  toTimeInput,
} from './dates';

describe('calendar dates', () => {
  it('validates real dates only', () => {
    expect(isIsoDate('2026-02-28')).toBe(true);
    expect(isIsoDate('2026-02-30')).toBe(false);
    expect(isIsoDate('2026-2-3')).toBe(false);
    expect(isIsoDate(20260228)).toBe(false);
  });

  it('adds days and months across boundaries', () => {
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
    expect(addMonths('2026-01-31', 1)).toBe('2026-02-28');
    expect(addMonths('2028-01-31', 1)).toBe('2028-02-29');
    expect(addMonths('2026-11-15', 2)).toBe('2027-01-15');
    expect(diffDays('2026-09-01', '2026-09-30')).toBe(29);
    expect(diffDays('2026-09-30', '2026-09-01')).toBe(-29);
  });

  it('finds the Monday of a week and lists the days', () => {
    expect(startOfWeek('2026-09-26')).toBe('2026-09-21'); // Saturday → Monday
    expect(startOfWeek('2026-09-21')).toBe('2026-09-21');
    expect(startOfWeek('2026-09-27')).toBe('2026-09-21'); // Sunday belongs to the week that started on Monday
    expect(datesBetween('2026-09-29', '2026-10-02')).toEqual(['2026-09-29', '2026-09-30', '2026-10-01', '2026-10-02']);
    expect(datesBetween('2026-10-02', '2026-09-29')).toEqual([]);
    expect(dayHeader('2026-09-21')).toEqual({ weekday: 'Mon', day: 21, month: 'Sep' });
  });

  it('labels months', () => {
    expect(monthLabel(2026, 9)).toBe('September 2026');
    expect(monthLabel(2026, 9, true)).toBe('Sep 2026');
    expect(monthKeyLabel('2026-02')).toBe('Feb 2026');
    expect(monthKeyLabel('Q1')).toBe('Q1');
    expect(monthEnd(2028, 2)).toBe('2028-02-29');
  });
});

describe('times and durations', () => {
  it('formats minutes compactly', () => {
    expect(formatMinutes(480)).toBe('8 h');
    expect(formatMinutes(485)).toBe('8 h 5 min');
    expect(formatMinutes(45)).toBe('45 min');
    expect(formatMinutes(0)).toBe('—');
    expect(formatMinutes(null)).toBe('—');
  });

  it('converts shift times for inputs and display', () => {
    expect(formatTimeOfDay('09:00:00')).toBe('9:00 AM');
    expect(formatTimeOfDay('21:30:00')).toBe('9:30 PM');
    expect(formatTimeOfDay('00:15')).toBe('12:15 AM');
    expect(toTimeInput('07:00:00')).toBe('07:00');
    expect(fromTimeInput('07:00')).toBe('07:00:00');
    expect(fromTimeInput('')).toBeNull();
  });

  it('reads date-time inputs as hospital time whatever the workstation zone', () => {
    expect(fromIstInput('2026-09-26T14:05')).toBe('2026-09-26T14:05:00+05:30');
    expect(fromIstInput('bad')).toBeNull();
    expect(toIstInput('2026-09-26T08:35:00Z')).toBe('2026-09-26T14:05');
    expect(toIstInput('2026-09-26T14:05:00+05:30')).toBe('2026-09-26T14:05');
    expect(toIstInput(null)).toBe('');
  });

  it('describes due dates', () => {
    expect(dueLabel(-3)).toBe('Overdue by 3 days');
    expect(dueLabel(-1)).toBe('Overdue by 1 day');
    expect(dueLabel(0)).toBe('Due today');
    expect(dueLabel(1)).toBe('Due in 1 day');
    expect(dueLabel(null)).toBe('—');
  });
});
