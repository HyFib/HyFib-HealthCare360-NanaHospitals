import type { StatusTone } from '@hc360/ui';
import type { AttendanceDayDto } from './api';
import { ATTENDANCE_FLAGS, type AttendanceFlag, type AttendanceStatus } from './labels';

const FLAG_BITS: Record<AttendanceFlag, number> = {
  Late: 1,
  EarlyExit: 2,
  MissingPunch: 4,
  Overtime: 8,
  WorkedOnOffDay: 16,
  Unrostered: 32,
  Regularised: 64,
};

/**
 * The server sends the [Flags] enum as "None" or "Late, MissingPunch" (JsonStringEnumConverter). Numbers are decoded
 * too, so a converter change on the server never breaks the sheet.
 */
export function parseAttendanceFlags(flags: string | number | null | undefined): AttendanceFlag[] {
  if (flags === null || flags === undefined || flags === 'None' || flags === '') {
    return [];
  }
  if (typeof flags === 'number') {
    return ATTENDANCE_FLAGS.filter((flag) => (flags & FLAG_BITS[flag]) !== 0);
  }
  const known = ATTENDANCE_FLAGS as readonly string[];
  return flags
    .split(',')
    .map((flag) => flag.trim())
    .filter((flag): flag is AttendanceFlag => known.includes(flag));
}

export function attendanceTone(status: AttendanceStatus): StatusTone {
  switch (status) {
    case 'Present':
    case 'OnDuty':
      return 'success';
    case 'Absent':
      return 'danger';
    case 'HalfDay':
      return 'warning';
    case 'Leave':
      return 'brand';
    default:
      return 'informative';
  }
}

export function attendanceFlagTone(flag: AttendanceFlag): StatusTone {
  switch (flag) {
    case 'MissingPunch':
      return 'danger';
    case 'Late':
    case 'EarlyExit':
      return 'warning';
    case 'Overtime':
    case 'WorkedOnOffDay':
      return 'brand';
    case 'Regularised':
      return 'success';
    default:
      return 'informative';
  }
}

/** Days that need HR attention before payroll: a missed punch or an absence without leave. */
export function needsAttention(row: { missingPunchDays: number; absentDays: number }): boolean {
  return row.missingPunchDays > 0 || row.absentDays > 0;
}

/** "1", "0.5", "12.5" — day fractions without trailing zeros. */
export function formatDays(value: number | null | undefined): string {
  if (value === null || value === undefined) {
    return '—';
  }
  return Number.isInteger(value) ? String(value) : value.toFixed(1).replace(/\.0$/, '');
}

/** Whether a day on the sheet can still be corrected by a regularisation request. */
export function canRegularise(day: { isLocked: boolean; status: AttendanceStatus; flags: string }): boolean {
  if (day.isLocked) {
    return false;
  }
  const flags = parseAttendanceFlags(day.flags);
  return day.status === 'Absent' || day.status === 'HalfDay' || flags.includes('MissingPunch') || flags.includes('Late') || flags.includes('EarlyExit');
}

/** Totals of a set of days (present, leave, absent…), as day fractions. */
export function attendanceTotals(rows: AttendanceDayDto[]): { present: number; paidLeave: number; unpaidLeave: number; absent: number; off: number; lateDays: number; missing: number } {
  const totals = { present: 0, paidLeave: 0, unpaidLeave: 0, absent: 0, off: 0, lateDays: 0, missing: 0 };
  for (const row of rows) {
    totals.present += row.presentDays;
    totals.paidLeave += row.paidLeaveDays;
    totals.unpaidLeave += row.unpaidLeaveDays;
    totals.absent += row.absentDays;
    totals.off += row.offDays;
    const flags = parseAttendanceFlags(row.flags);
    if (flags.includes('Late')) {
      totals.lateDays += 1;
    }
    if (flags.includes('MissingPunch')) {
      totals.missing += 1;
    }
  }
  return totals;
}
