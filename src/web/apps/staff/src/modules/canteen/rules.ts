import { previousMonth } from '../inpatient/time';

/** Canteen input rules mirrored from the server (`StaffCreditRules`), for instant feedback at the counter. */

/** Employee code as the server checks it: a letter or digit first, then letters, digits, "/" or "-" (2–20 characters). */
export function isValidEmployeeCode(code: string): boolean {
  return /^[A-Za-z0-9][A-Za-z0-9/-]{1,19}$/.test(code.trim());
}

/** Upper-case, trimmed code, as the server stores it. */
export function normalizeEmployeeCode(code: string): string {
  return code.trim().toUpperCase();
}

/** "yyyy-MM" payroll period. */
export function isValidPeriod(period: string): boolean {
  return /^\d{4}-(0[1-9]|1[0-2])$/.test(period);
}

/** A month can be settled with payroll only after it has ended (period before the current month). */
export function canSettle(period: string, currentMonth: string): boolean {
  return isValidPeriod(period) && period < currentMonth;
}

/** "2026-09" → "September 2026". */
export function monthLabel(period: string): string {
  const [year, month] = period.split('-').map(Number);
  if (!year || !month) {
    return period;
  }
  return new Intl.DateTimeFormat('en-IN', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(Date.UTC(year, month - 1, 1));
}

/** The last `count` months ending with `current` ("yyyy-MM"), newest first. */
export function recentMonths(current: string, count: number): string[] {
  const months = [current];
  while (months.length < count) {
    months.push(previousMonth(months[months.length - 1] ?? current));
  }
  return months;
}
