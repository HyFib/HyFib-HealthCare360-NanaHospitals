import type { PhysioReferralDetail, PhysioSessionDto, TreatmentPlanDto } from './api';

/**
 * Pure physiotherapy rules for the screens, mirroring `TreatmentPlan`, `PhysioReferral` and the session scheduler.
 */

export interface PlanProgress {
  planned: number;
  /** Sessions that count against the plan (all except cancelled ones). */
  booked: number;
  attended: number;
  missed: number;
  /** How many more sessions the plan allows to be booked. */
  remaining: number;
}

export function planProgress(
  plan: Pick<TreatmentPlanDto, 'sessionsPlanned'> | null,
  sessions: readonly Pick<PhysioSessionDto, 'status'>[],
): PlanProgress {
  const planned = plan?.sessionsPlanned ?? 0;
  const booked = sessions.filter((s) => s.status !== 'Cancelled').length;
  return {
    planned,
    booked,
    attended: sessions.filter((s) => s.status === 'Attended').length,
    missed: sessions.filter((s) => s.status === 'Missed').length,
    remaining: Math.max(0, planned - booked),
  };
}

/** The most sessions one scheduling request may add: what the plan still allows, and at most 30 at a time. */
export function scheduleLimit(
  plan: Pick<TreatmentPlanDto, 'sessionsPlanned'> | null,
  sessions: readonly Pick<PhysioSessionDto, 'status'>[],
): number {
  return Math.min(30, planProgress(plan, sessions).remaining);
}

/** Server default spacing: 7 days divided by sessions per week, whole days, at least 1 (3 a week → every 2 days). */
export function defaultIntervalDays(sessionsPerWeek: number): number {
  return Math.max(1, Math.floor(7 / Math.max(1, sessionsPerWeek)));
}

/** The session times a scheduling request will create. */
export function sessionTimes(firstSessionAt: string, count: number, intervalDays: number): string[] {
  const start = Date.parse(firstSessionAt);
  if (Number.isNaN(start) || count < 1) {
    return [];
  }
  return Array.from({ length: count }, (_, i) => new Date(start + i * intervalDays * 86_400_000).toISOString());
}

export type ReferralAction = 'assess' | 'plan' | 'schedule' | 'treat' | 'complete' | 'closed';

export interface NextStep {
  action: ReferralAction;
  message: string;
}

/** What the physiotherapist should do next with a referral. */
export function nextStep(detail: Pick<PhysioReferralDetail, 'status' | 'assessments' | 'plan' | 'sessions'>): NextStep {
  if (detail.status === 'Completed' || detail.status === 'Cancelled') {
    return {
      action: 'closed',
      message: detail.status === 'Completed' ? 'Treatment is complete.' : 'This referral was cancelled.',
    };
  }
  if (detail.assessments.length === 0 || detail.status === 'New') {
    return { action: 'assess', message: 'Assess the patient first: treatment is never planned without an assessment.' };
  }
  if (!detail.plan || detail.status === 'Assessed') {
    return { action: 'plan', message: 'Write the treatment plan: modalities, number of sessions and frequency.' };
  }
  const progress = planProgress(detail.plan, detail.sessions);
  const upcoming = detail.sessions.filter((s) => s.status === 'Scheduled').length;
  if (upcoming > 0) {
    return {
      action: 'treat',
      message: `${upcoming} session${upcoming === 1 ? ' is' : 's are'} booked. Record each one on the day.`,
    };
  }
  if (progress.remaining > 0) {
    return {
      action: 'schedule',
      message: `Book the remaining ${progress.remaining} session${progress.remaining === 1 ? '' : 's'} of the plan.`,
    };
  }
  return {
    action: 'complete',
    message:
      'All planned sessions are done. Complete the referral with a discharge summary, or revise the plan to add sessions.',
  };
}

/** A referral can be cancelled only before any treatment was given (then it must be completed instead). */
export function canCancelReferral(detail: Pick<PhysioReferralDetail, 'status' | 'sessions'>): boolean {
  return (
    detail.status !== 'Completed' &&
    detail.status !== 'Cancelled' &&
    !detail.sessions.some((s) => s.status === 'Attended')
  );
}

/** Change in the 0–10 pain score within a session (negative = better). */
export function painChange(before: number | null, after: number | null): number | null {
  return before === null || after === null ? null : after - before;
}
