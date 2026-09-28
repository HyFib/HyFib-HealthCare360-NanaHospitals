/**
 * Types shared by the four specialty-care modules (they mirror SpecialtyCare `Domain/Common`).
 */

/** Where the billing hand-off of a clinical event stands. Clinical work never waits for billing. */
export type ChargeStatus = 'NotDue' | 'Posted' | 'Failed' | 'Cancelled';

/** Result of a `/charges/retry` call. */
export interface ChargeOutcome {
  status: ChargeStatus;
  error: string | null;
}

/** Clinical order priority (Clinical module), used by physiotherapy referrals. */
export type OrderPriority = 'Routine' | 'Urgent' | 'Stat';

export type Weekday = 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday' | 'Sunday';

export const WEEKDAYS: readonly Weekday[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

export const chargeStatusLabel: Record<ChargeStatus, string> = {
  NotDue: 'Not billed yet',
  Posted: 'Billed',
  Failed: 'Billing failed',
  Cancelled: 'Charge withdrawn',
};
