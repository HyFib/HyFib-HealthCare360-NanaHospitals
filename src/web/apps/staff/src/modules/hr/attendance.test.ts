import { describe, expect, it } from 'vitest';
import type { AttendanceDayDto } from './api';
import {
  attendanceFlagTone,
  attendanceTone,
  attendanceTotals,
  canRegularise,
  formatDays,
  needsAttention,
  parseAttendanceFlags,
} from './attendance';
import { credentialExpiry } from './labels';

describe('attendance flags', () => {
  it('reads the server flags text', () => {
    expect(parseAttendanceFlags('None')).toEqual([]);
    expect(parseAttendanceFlags('Late, MissingPunch')).toEqual(['Late', 'MissingPunch']);
    expect(parseAttendanceFlags('Overtime,WorkedOnOffDay')).toEqual(['Overtime', 'WorkedOnOffDay']);
    expect(parseAttendanceFlags('Late, Unknown')).toEqual(['Late']);
    expect(parseAttendanceFlags(null)).toEqual([]);
  });

  it('decodes numeric flags too', () => {
    expect(parseAttendanceFlags(5)).toEqual(['Late', 'MissingPunch']);
    expect(parseAttendanceFlags(0)).toEqual([]);
    expect(parseAttendanceFlags(64)).toEqual(['Regularised']);
  });

  it('colours statuses and flags', () => {
    expect(attendanceTone('Absent')).toBe('danger');
    expect(attendanceTone('Present')).toBe('success');
    expect(attendanceTone('WeekOff')).toBe('informative');
    expect(attendanceFlagTone('MissingPunch')).toBe('danger');
    expect(attendanceFlagTone('Late')).toBe('warning');
  });
});

describe('attendance sheet helpers', () => {
  it('flags rows that need attention before payroll', () => {
    expect(needsAttention({ missingPunchDays: 1, absentDays: 0 })).toBe(true);
    expect(needsAttention({ missingPunchDays: 0, absentDays: 0.5 })).toBe(true);
    expect(needsAttention({ missingPunchDays: 0, absentDays: 0 })).toBe(false);
  });

  it('formats day fractions', () => {
    expect(formatDays(12)).toBe('12');
    expect(formatDays(0.5)).toBe('0.5');
    expect(formatDays(12.5)).toBe('12.5');
    expect(formatDays(null)).toBe('—');
  });

  it('offers regularisation only for correctable days', () => {
    expect(canRegularise({ isLocked: false, status: 'Absent', flags: 'MissingPunch' })).toBe(true);
    expect(canRegularise({ isLocked: false, status: 'Present', flags: 'Late' })).toBe(true);
    expect(canRegularise({ isLocked: false, status: 'Present', flags: 'None' })).toBe(false);
    expect(canRegularise({ isLocked: true, status: 'Absent', flags: 'MissingPunch' })).toBe(false);
  });
});

describe('credential expiry wording', () => {
  it('describes every stage', () => {
    expect(credentialExpiry('Expired', -3)).toEqual({ label: 'Expired 3 days ago', tone: 'danger' });
    expect(credentialExpiry('DueIn7Days', 0)).toEqual({ label: 'Expires today', tone: 'danger' });
    expect(credentialExpiry('DueIn30Days', 20)).toEqual({ label: 'Expires in 20 days', tone: 'warning' });
    expect(credentialExpiry('None', 200)).toEqual({ label: 'Valid', tone: 'success' });
    expect(credentialExpiry('None', null)).toEqual({ label: 'No expiry', tone: 'informative' });
  });
});

describe('attendance totals', () => {
  it('adds day fractions and counts late and missed-punch days', () => {
    const day = (partial: Partial<AttendanceDayDto>): AttendanceDayDto => ({
      id: String(Math.random()),
      employeeId: 'e',
      employeeCode: 'E',
      employeeName: 'E',
      date: '2026-09-01',
      status: 'Present',
      shiftCode: 'GEN',
      firstIn: null,
      lastOut: null,
      workedMinutes: 0,
      lateMinutes: 0,
      earlyExitMinutes: 0,
      overtimeMinutes: 0,
      presentDays: 0,
      paidLeaveDays: 0,
      unpaidLeaveDays: 0,
      absentDays: 0,
      offDays: 0,
      flags: 'None',
      punchCount: 0,
      isLocked: false,
      ...partial,
    });
    const totals = attendanceTotals([
      day({ presentDays: 1, flags: 'Late' }),
      day({ presentDays: 0.5, paidLeaveDays: 0.5 }),
      day({ absentDays: 1, flags: 'MissingPunch' }),
      day({ offDays: 1 }),
    ]);
    expect(totals).toEqual({
      present: 1.5,
      paidLeave: 0.5,
      unpaidLeave: 0,
      absent: 1,
      off: 1,
      lateDays: 1,
      missing: 1,
    });
  });
});
