import { describe, expect, it } from 'vitest';
import type { PhysioSessionStatus } from './api';
import {
  canCancelReferral,
  defaultIntervalDays,
  nextStep,
  painChange,
  planProgress,
  scheduleLimit,
  sessionTimes,
} from './logic';

const sessions = (...statuses: PhysioSessionStatus[]) => statuses.map((status) => ({ status }));

describe('treatment plan progress (mirrors TreatmentPlan.EnsureCanSchedule)', () => {
  it('counts every session except cancelled ones against the plan', () => {
    expect(
      planProgress({ sessionsPlanned: 10 }, sessions('Attended', 'Attended', 'Missed', 'Cancelled', 'Scheduled')),
    ).toEqual({
      planned: 10,
      booked: 4,
      attended: 2,
      missed: 1,
      remaining: 6,
    });
  });

  it('never allows booking beyond the plan, and at most 30 at once', () => {
    expect(scheduleLimit({ sessionsPlanned: 10 }, sessions('Scheduled', 'Scheduled'))).toBe(8);
    expect(scheduleLimit({ sessionsPlanned: 60 }, [])).toBe(30);
    expect(scheduleLimit({ sessionsPlanned: 2 }, sessions('Attended', 'Scheduled'))).toBe(0);
    expect(scheduleLimit(null, [])).toBe(0);
  });

  it('spaces sessions like the server (whole days, at least one)', () => {
    expect(defaultIntervalDays(3)).toBe(2);
    expect(defaultIntervalDays(5)).toBe(1);
    expect(defaultIntervalDays(1)).toBe(7);
    expect(defaultIntervalDays(14)).toBe(1);
    expect(defaultIntervalDays(0)).toBe(7);
  });

  it('previews the session times', () => {
    expect(sessionTimes('2026-09-28T04:30:00.000Z', 3, 2)).toEqual([
      '2026-09-28T04:30:00.000Z',
      '2026-09-30T04:30:00.000Z',
      '2026-10-02T04:30:00.000Z',
    ]);
    expect(sessionTimes('bad', 3, 2)).toEqual([]);
  });
});

describe('referral workflow', () => {
  const base = { status: 'New' as const, assessments: [], plan: null, sessions: [] };

  it('asks for an assessment before any plan', () => {
    expect(nextStep(base).action).toBe('assess');
  });

  it('asks for a plan once assessed', () => {
    expect(nextStep({ ...base, status: 'Assessed', assessments: [{ id: 'a' }] as never }).action).toBe('plan');
  });

  it('asks to book sessions, then to treat, then to complete', () => {
    const plan = { sessionsPlanned: 2 } as never;
    const assessed = { ...base, status: 'InTreatment' as const, assessments: [{ id: 'a' }] as never, plan };
    expect(nextStep({ ...assessed, sessions: [] }).action).toBe('schedule');
    expect(nextStep({ ...assessed, sessions: sessions('Scheduled') as never }).action).toBe('treat');
    expect(nextStep({ ...assessed, sessions: sessions('Attended', 'Attended') as never }).action).toBe('complete');
  });

  it('refuses to cancel once treatment was given', () => {
    expect(canCancelReferral({ status: 'InTreatment', sessions: sessions('Scheduled', 'Missed') as never })).toBe(true);
    expect(canCancelReferral({ status: 'InTreatment', sessions: sessions('Attended') as never })).toBe(false);
    expect(canCancelReferral({ status: 'Completed', sessions: [] })).toBe(false);
  });

  it('measures pain change within a session', () => {
    expect(painChange(7, 4)).toBe(-3);
    expect(painChange(null, 4)).toBeNull();
  });
});
