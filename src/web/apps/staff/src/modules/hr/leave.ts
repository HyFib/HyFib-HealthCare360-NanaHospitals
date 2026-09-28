/** Pure leave helpers for the apply form: a day estimate, session rules and balance lookups. */
import { formatDateOnly, type Gender } from '@hc360/core';
import type { LeaveBalanceDto, LeaveTypeDto } from './api';
import type { LeaveSession } from './labels';
import { datesBetween, isIsoDate } from './shared/dates';

/**
 * Days asked for, before the server removes holidays and weekly offs (it does that for every type that does not count
 * calendar days). Starting in the second half or ending in the first half removes half a day each.
 */
export function estimateLeaveDays(from: string, fromSession: LeaveSession, to: string, toSession: LeaveSession): number | null {
  if (!isIsoDate(from) || !isIsoDate(to) || to < from) {
    return null;
  }
  if (from === to && fromSession === 'SecondHalf' && toSession === 'FirstHalf') {
    return null;
  }
  let days = datesBetween(from, to).length;
  if (fromSession === 'SecondHalf') {
    days -= 0.5;
  }
  if (toSession === 'FirstHalf') {
    days -= 0.5;
  }
  return days;
}

/** Leave types this employee may apply for (active, and for the right gender where the type is gender-specific). */
export function applicableLeaveTypes(types: LeaveTypeDto[], gender?: Gender | null): LeaveTypeDto[] {
  return types.filter((type) => type.isActive && (!type.applicableGender || !gender || type.applicableGender === gender));
}

export function balanceFor(balances: LeaveBalanceDto[] | undefined, leaveTypeId: string | null | undefined): LeaveBalanceDto | undefined {
  return leaveTypeId ? balances?.find((balance) => balance.leaveTypeId === leaveTypeId) : undefined;
}

/** "3 days", "0.5 day", "12.5 days". */
export function daysText(days: number): string {
  const text = Number.isInteger(days) ? String(days) : days.toFixed(1);
  return `${text} day${days === 1 || days === 0.5 ? '' : 's'}`;
}

/** Whether the request needs a supporting document for the (estimated) number of days. */
export function needsDocument(type: LeaveTypeDto | undefined, days: number | null): boolean {
  return Boolean(type && type.documentRequiredAfterDays !== null && days !== null && days > type.documentRequiredAfterDays);
}

/**
 * Employees may withdraw pending leave, or approved leave that has not started; HR may cancel approved leave at any
 * time (the server's LeaveRequest.Cancel rule).
 */
export function canCancelLeave(request: { status: string; fromDate: string }, today: string, byHr: boolean): boolean {
  return request.status === 'Pending' || (request.status === 'Approved' && (byHr || request.fromDate > today));
}

/** "12 Sep 2026 (second half) to 14 Sep 2026 (first half)" pieces: which half, if any, to mention. */
export function sessionNote(session: LeaveSession, edge: 'from' | 'to'): string {
  if (edge === 'from' && session === 'SecondHalf') {
    return ' (from the second half)';
  }
  if (edge === 'to' && session === 'FirstHalf') {
    return ' (first half only)';
  }
  return '';
}

/** "12 Sept 2026 (from the second half) to 14 Sept 2026" or a single day with its half. */
export function leavePeriodText(r: { fromDate: string; fromSession: LeaveSession; toDate: string; toSession: LeaveSession }): string {
  if (r.fromDate === r.toDate) {
    const half = r.fromSession === 'SecondHalf' ? ' (second half)' : r.toSession === 'FirstHalf' ? ' (first half)' : '';
    return `${formatDateOnly(r.fromDate)}${half}`;
  }
  return `${formatDateOnly(r.fromDate)}${sessionNote(r.fromSession, 'from')} to ${formatDateOnly(r.toDate)}${sessionNote(r.toSession, 'to')}`;
}
