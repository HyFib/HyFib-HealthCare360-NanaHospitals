import { describe, expect, it } from 'vitest';
import type { LeaveTypeDto } from './api';
import {
  applicableLeaveTypes,
  balanceFor,
  canCancelLeave,
  daysText,
  estimateLeaveDays,
  leavePeriodText,
  needsDocument,
  sessionNote,
} from './leave';

function type(partial: Partial<LeaveTypeDto>): LeaveTypeDto {
  return {
    id: 'cl',
    code: 'CL',
    name: 'Casual leave',
    annualQuota: 12,
    carryForwardCap: 0,
    isEncashable: false,
    isPaid: true,
    allowHalfDay: true,
    tracksBalance: true,
    countsNonWorkingDays: false,
    maxDaysPerRequest: null,
    documentRequiredAfterDays: null,
    applicableGender: null,
    isActive: true,
    version: 1,
    ...partial,
  };
}

describe('leave day estimate', () => {
  it('counts calendar days with half-day sessions', () => {
    expect(estimateLeaveDays('2026-09-28', 'FirstHalf', '2026-09-30', 'SecondHalf')).toBe(3);
    expect(estimateLeaveDays('2026-09-28', 'SecondHalf', '2026-09-30', 'SecondHalf')).toBe(2.5);
    expect(estimateLeaveDays('2026-09-28', 'FirstHalf', '2026-09-28', 'FirstHalf')).toBe(0.5);
    expect(estimateLeaveDays('2026-09-28', 'SecondHalf', '2026-09-28', 'SecondHalf')).toBe(0.5);
  });

  it('rejects impossible ranges', () => {
    expect(estimateLeaveDays('2026-09-28', 'SecondHalf', '2026-09-28', 'FirstHalf')).toBeNull();
    expect(estimateLeaveDays('2026-09-30', 'FirstHalf', '2026-09-28', 'SecondHalf')).toBeNull();
    expect(estimateLeaveDays('', 'FirstHalf', '2026-09-28', 'SecondHalf')).toBeNull();
  });

  it('writes days in words', () => {
    expect(daysText(1)).toBe('1 day');
    expect(daysText(0.5)).toBe('0.5 day');
    expect(daysText(2.5)).toBe('2.5 days');
  });
});

describe('leave rules', () => {
  it('offers only active types for the right gender', () => {
    const types = [
      type({ id: 'cl' }),
      type({ id: 'ml', applicableGender: 'Female' }),
      type({ id: 'old', isActive: false }),
    ];
    expect(applicableLeaveTypes(types, 'Male').map((t) => t.id)).toEqual(['cl']);
    expect(applicableLeaveTypes(types, 'Female').map((t) => t.id)).toEqual(['cl', 'ml']);
    expect(applicableLeaveTypes(types, null).map((t) => t.id)).toEqual(['cl', 'ml']);
  });

  it('asks for a document above the type limit', () => {
    expect(needsDocument(type({ documentRequiredAfterDays: 2 }), 3)).toBe(true);
    expect(needsDocument(type({ documentRequiredAfterDays: 2 }), 2)).toBe(false);
    expect(needsDocument(type({}), 30)).toBe(false);
  });

  it('knows who can cancel what', () => {
    expect(canCancelLeave({ status: 'Pending', fromDate: '2026-09-01' }, '2026-09-27', false)).toBe(true);
    expect(canCancelLeave({ status: 'Approved', fromDate: '2026-09-28' }, '2026-09-27', false)).toBe(true);
    expect(canCancelLeave({ status: 'Approved', fromDate: '2026-09-27' }, '2026-09-27', false)).toBe(false);
    expect(canCancelLeave({ status: 'Approved', fromDate: '2026-09-01' }, '2026-09-27', true)).toBe(true);
    expect(canCancelLeave({ status: 'Rejected', fromDate: '2026-10-01' }, '2026-09-27', true)).toBe(false);
  });

  it('finds balances and session notes', () => {
    expect(balanceFor(undefined, 'cl')).toBeUndefined();
    expect(sessionNote('SecondHalf', 'from')).toBe(' (from the second half)');
    expect(sessionNote('FirstHalf', 'to')).toBe(' (first half only)');
    expect(sessionNote('FirstHalf', 'from')).toBe('');
  });
});

describe('leave period text', () => {
  it('describes single and multi-day leave with halves', () => {
    expect(
      leavePeriodText({
        fromDate: '2026-09-28',
        fromSession: 'FirstHalf',
        toDate: '2026-09-28',
        toSession: 'FirstHalf',
      }),
    ).toMatch(/28 Sep.*2026 \(first half\)/);
    expect(
      leavePeriodText({
        fromDate: '2026-09-28',
        fromSession: 'SecondHalf',
        toDate: '2026-09-30',
        toSession: 'SecondHalf',
      }),
    ).toMatch(/28 Sep.*2026 \(from the second half\) to 30 Sep.*2026$/);
  });
});
