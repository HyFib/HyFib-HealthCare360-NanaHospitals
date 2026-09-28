import { describe, expect, it } from 'vitest';
import {
  addDays,
  dateRange,
  dayParts,
  daysBetween,
  formatClock,
  formatDuration,
  formatHour,
  isIsoDate,
  isoToIstLocal,
  isoWeekday,
  istDate,
  istDateTime,
  istLocalToIso,
  minutesSince,
  minutesToTime,
  relativeDayLabel,
  startOfWeek,
  timeToMinutes,
  toInputTime,
  toServerTime,
} from './dates';

describe('calendar dates', () => {
  it('validates ISO calendar dates, including impossible days', () => {
    expect(isIsoDate('2026-09-26')).toBe(true);
    expect(isIsoDate('2026-02-30')).toBe(false);
    expect(isIsoDate('26-09-2026')).toBe(false);
    expect(isIsoDate(20260926)).toBe(false);
  });

  it('adds days across month and year ends', () => {
    expect(addDays('2026-09-30', 1)).toBe('2026-10-01');
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
    expect(daysBetween('2026-09-26', '2026-10-10')).toBe(14);
    expect(dateRange('2026-09-29', 3)).toEqual(['2026-09-29', '2026-09-30', '2026-10-01']);
  });

  it('uses Monday-first weekdays like the server', () => {
    expect(isoWeekday('2026-09-28')).toBe(1); // Monday
    expect(isoWeekday('2026-09-27')).toBe(7); // Sunday
    expect(startOfWeek('2026-09-27')).toBe('2026-09-21');
    expect(startOfWeek('2026-09-28')).toBe('2026-09-28');
  });

  it('labels date chips', () => {
    expect(dayParts('2026-09-26')).toMatchObject({ weekday: 'Sat', day: 26 });
    expect(relativeDayLabel('2026-09-26', '2026-09-26')).toBe('Today');
    expect(relativeDayLabel('2026-09-27', '2026-09-26')).toBe('Tomorrow');
    expect(relativeDayLabel('2026-09-25', '2026-09-26')).toBe('Yesterday');
  });
});

describe('IST instants', () => {
  it('reads the IST date and minute of an instant whatever the offset', () => {
    // 20:00 UTC on 25 Sep is 01:30 IST on 26 Sep.
    expect(istDateTime('2026-09-25T20:00:00+00:00')).toEqual({ date: '2026-09-26', minutes: 90 });
    expect(istDateTime('2026-09-26T09:15:00+05:30')).toEqual({ date: '2026-09-26', minutes: 555 });
    expect(istDate('2026-09-26T18:29:59Z')).toBe('2026-09-26');
    expect(istDate('2026-09-26T18:30:00Z')).toBe('2026-09-27');
  });

  it('converts datetime-local input values as IST', () => {
    expect(istLocalToIso('2026-09-26T10:30')).toBe('2026-09-26T10:30:00+05:30');
    expect(istLocalToIso('2026-02-30T10:30')).toBeNull();
    expect(istLocalToIso('')).toBeNull();
    expect(isoToIstLocal('2026-09-26T05:00:00Z')).toBe('2026-09-26T10:30');
  });
});

describe('wall-clock times', () => {
  it('parses and prints server TimeOnly values', () => {
    expect(timeToMinutes('09:30:00')).toBe(570);
    expect(timeToMinutes('9:05')).toBe(545);
    expect(timeToMinutes('24:00')).toBeNaN();
    expect(timeToMinutes('')).toBeNaN();
    expect(minutesToTime(570)).toBe('09:30');
    expect(toServerTime('09:30')).toBe('09:30:00');
    expect(toInputTime('17:45:00')).toBe('17:45');
    expect(toInputTime(null)).toBe('');
  });

  it('formats times the way the rest of the app does', () => {
    expect(formatClock('09:30:00')).toBe('9:30 am');
    expect(formatClock('12:00:00')).toBe('12:00 pm');
    expect(formatClock('00:15')).toBe('12:15 am');
    expect(formatClock('13:05')).toBe('1:05 pm');
    expect(formatClock('nonsense')).toBe('—');
    expect(formatHour(540)).toBe('9 am');
    expect(formatHour(780)).toBe('1 pm');
  });

  it('measures waiting time and durations', () => {
    const now = Date.parse('2026-09-26T10:00:00+05:30');
    expect(minutesSince('2026-09-26T09:52:30+05:30', now)).toBe(7);
    expect(minutesSince('2026-09-26T10:05:00+05:30', now)).toBe(0);
    expect(minutesSince(null, now)).toBe(0);
    expect(formatDuration(45)).toBe('45 min');
    expect(formatDuration(60)).toBe('1 h');
    expect(formatDuration(65)).toBe('1 h 5 min');
  });
});
