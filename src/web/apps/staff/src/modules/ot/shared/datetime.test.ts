import { describe, expect, it } from 'vitest';
import {
  addDays,
  addMinutesIso,
  dayLabel,
  daysBetween,
  formatClock,
  formatDuration,
  fromIstInput,
  istDate,
  istDateTimeToIso,
  istTime,
  isoWeekday,
  minutesBetween,
  minutesIntoIstDay,
  startOfWeek,
  toIstInput,
} from './datetime';

describe('IST input conversion', () => {
  it('shows an instant as IST wall-clock time', () => {
    expect(toIstInput('2026-09-26T03:30:00Z')).toBe('2026-09-26T09:00');
    expect(toIstInput('2026-09-25T20:00:00+00:00')).toBe('2026-09-26T01:30');
    expect(toIstInput(null)).toBe('');
    expect(toIstInput('not a date')).toBe('');
  });

  it('reads a datetime-local value as IST whatever the workstation zone', () => {
    expect(fromIstInput('2026-09-26T09:00')).toBe('2026-09-26T03:30:00.000Z');
    expect(fromIstInput('2026-09-26T09:00:30')).toBe('2026-09-26T03:30:30.000Z');
    expect(fromIstInput('2026-09-26')).toBeNull();
    expect(fromIstInput('')).toBeNull();
    expect(fromIstInput(undefined)).toBeNull();
  });

  it('round-trips', () => {
    const iso = fromIstInput('2026-12-31T23:45');
    expect(iso).not.toBeNull();
    expect(toIstInput(iso)).toBe('2026-12-31T23:45');
  });

  it('combines a date and a time', () => {
    expect(istDateTimeToIso('2026-09-26', '23:30')).toBe('2026-09-26T18:00:00.000Z');
    expect(istDateTimeToIso('2026-09-26', '9:30')).toBeNull();
    expect(istDateTimeToIso(null, '09:30')).toBeNull();
  });

  it('gives the IST date and time of an instant', () => {
    expect(istDate('2026-09-25T20:00:00Z')).toBe('2026-09-26');
    expect(istTime('2026-09-25T20:00:00Z')).toBe('01:30');
  });
});

describe('calendar arithmetic', () => {
  it('counts minutes into the IST day', () => {
    expect(minutesIntoIstDay('2026-09-26T03:30:00Z', '2026-09-26')).toBe(540);
    expect(minutesIntoIstDay('2026-09-25T18:00:00Z', '2026-09-26')).toBe(-30);
    expect(minutesIntoIstDay('2026-09-26T19:00:00Z', '2026-09-26')).toBe(1470);
  });

  it('adds days across month and year ends', () => {
    expect(addDays('2026-09-30', 1)).toBe('2026-10-01');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
  });

  it('finds the Monday of the week (server week view runs Monday–Sunday)', () => {
    expect(isoWeekday('2026-09-26')).toBe(6);
    expect(isoWeekday('2026-09-27')).toBe(7);
    expect(startOfWeek('2026-09-26')).toBe('2026-09-21');
    expect(startOfWeek('2026-09-27')).toBe('2026-09-21');
    expect(startOfWeek('2026-09-21')).toBe('2026-09-21');
  });

  it('measures spans', () => {
    expect(daysBetween('2026-09-21', '2026-09-27')).toBe(6);
    expect(minutesBetween('2026-09-26T03:30:00Z', '2026-09-26T05:45:00Z')).toBe(135);
    expect(addMinutesIso('2026-09-26T03:30:00.000Z', 90)).toBe('2026-09-26T05:00:00.000Z');
  });
});

describe('display', () => {
  it('formats durations', () => {
    expect(formatDuration(135)).toBe('2 h 15 min');
    expect(formatDuration(45)).toBe('45 min');
    expect(formatDuration(120)).toBe('2 h');
    expect(formatDuration(null)).toBe('—');
  });

  it('formats clock times for the calendar gutter', () => {
    expect(formatClock(540)).toBe('9 am');
    expect(formatClock(555)).toBe('9:15 am');
    expect(formatClock(0)).toBe('12 am');
    expect(formatClock(720)).toBe('12 pm');
    expect(formatClock(780)).toBe('1 pm');
    expect(formatClock(1440)).toBe('12 am');
  });

  it('labels a day', () => {
    const label = dayLabel('2026-09-26');
    expect(label).toMatch(/Sat/);
    expect(label).toMatch(/26/);
  });
});
