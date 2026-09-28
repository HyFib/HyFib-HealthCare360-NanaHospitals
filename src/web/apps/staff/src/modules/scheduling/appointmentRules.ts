import { formatTime } from '@hc360/core';
import type { AppointmentStatus } from './api';
import { istDate, type IsoDate } from './dates';

/**
 * Which desk actions an appointment allows right now. Mirrors the server's appointment state machine
 * (`Appointment.cs`) so buttons are disabled with a reason instead of failing after a click; the server still
 * enforces every rule.
 */

export type AppointmentAction = 'checkIn' | 'confirm' | 'reschedule' | 'cancel' | 'noShow';

export interface ActionAvailability {
  allowed: boolean;
  /** Why the action is not available (shown as a tooltip). */
  reason?: string;
}

export interface ActionContext {
  /** Today on the IST calendar. */
  today: IsoDate;
  /** Current time in epoch milliseconds. */
  now: number;
  /** Holds `scheduling.appointments.manage`. */
  canManage: boolean;
}

/** Booked or confirmed: the patient has not arrived yet, so the booking can still be changed. */
export function isOpen(status: AppointmentStatus): boolean {
  return status === 'Booked' || status === 'Confirmed';
}

/** Still holds its seat in the slot (everything except cancelled and rescheduled). */
export function holdsSeat(status: AppointmentStatus): boolean {
  return status !== 'Cancelled' && status !== 'Rescheduled';
}

function closedReason(status: AppointmentStatus): string {
  switch (status) {
    case 'CheckedIn':
    case 'InConsultation':
      return 'The patient has already checked in.';
    case 'Completed':
      return 'This appointment is completed.';
    case 'Cancelled':
      return 'This appointment was cancelled.';
    case 'NoShow':
      return 'This appointment was marked as a no-show.';
    case 'Rescheduled':
      return 'This appointment was moved to a new time.';
    default:
      return 'This appointment cannot be changed.';
  }
}

const allowed: ActionAvailability = { allowed: true };
const deny = (reason: string): ActionAvailability => ({ allowed: false, reason });

export function appointmentActions(
  appointment: { status: AppointmentStatus; startsAt: string },
  context: ActionContext,
): Record<AppointmentAction, ActionAvailability> {
  if (!context.canManage) {
    const noPermission = deny('You do not have permission to change appointments.');
    return { checkIn: noPermission, confirm: noPermission, reschedule: noPermission, cancel: noPermission, noShow: noPermission };
  }

  const { status, startsAt } = appointment;
  const open = isOpen(status);
  const closed = deny(closedReason(status));
  const isToday = istDate(startsAt) === context.today;

  return {
    checkIn: !open ? closed : isToday ? allowed : deny("Only today's appointments can be checked in. Reschedule it to today first."),
    confirm: status === 'Booked' ? allowed : status === 'Confirmed' ? deny('Already confirmed.') : closed,
    reschedule: open ? allowed : closed,
    cancel: open ? allowed : closed,
    noShow: !open ? closed : context.now >= Date.parse(startsAt) ? allowed : deny(`A no-show can be recorded after ${formatTime(startsAt)}.`),
  };
}

/** The one action the desk most likely wants next, shown as the row's button. */
export function primaryAction(appointment: { status: AppointmentStatus; startsAt: string }, context: ActionContext): AppointmentAction | null {
  const actions = appointmentActions(appointment, context);
  if (actions.checkIn.allowed) {
    return 'checkIn';
  }
  if (actions.confirm.allowed && istDate(appointment.startsAt) > context.today) {
    return 'confirm';
  }
  return null;
}
