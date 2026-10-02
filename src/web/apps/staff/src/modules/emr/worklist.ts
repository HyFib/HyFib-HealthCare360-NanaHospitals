import type { WardRoundPatient } from '@/modules/inpatient/api';
import type { AppointmentStatus } from '@/modules/scheduling/api';
import type { WorklistAppointment } from './api';

/**
 * The doctor's worklist (EMR-01): its three lists — today's queue, the appointments and the ward round — and the
 * plain-language summaries and ordering they use. Pure and unit-tested.
 */

export const WORKLIST_VIEWS = ['queue', 'appointments', 'rounds'] as const;
export type WorklistView = (typeof WORKLIST_VIEWS)[number];

export function parseWorklistView(value: unknown): WorklistView | undefined {
  return typeof value === 'string' && (WORKLIST_VIEWS as readonly string[]).includes(value)
    ? (value as WorklistView)
    : undefined;
}

// ---------------------------------------------------------------------------------------------------- appointments

/** Appointment statuses in the doctor's words. */
export const appointmentStatusLabel: Record<AppointmentStatus, string> = {
  Booked: 'Booked',
  Confirmed: 'Confirmed',
  CheckedIn: 'Arrived',
  InConsultation: 'With the doctor',
  Completed: 'Seen',
  Cancelled: 'Cancelled',
  NoShow: 'Did not come',
  Rescheduled: 'Moved',
};

/** A patient counts as late this long after the slot starts. */
export const LATE_AFTER_MINUTES = 15;

/** Not arrived yet, though the slot started more than a quarter of an hour ago. */
export function isLate(appointment: Pick<WorklistAppointment, 'status' | 'startsAt'>, now: Date): boolean {
  return (
    (appointment.status === 'Booked' || appointment.status === 'Confirmed') &&
    now.getTime() - new Date(appointment.startsAt).getTime() > LATE_AFTER_MINUTES * 60_000
  );
}

/** The consultation can be started or continued: the patient has arrived and has not been seen or sent away. */
export function canConsult(appointment: Pick<WorklistAppointment, 'status' | 'visitId'>): boolean {
  return (
    appointment.visitId !== null && (appointment.status === 'CheckedIn' || appointment.status === 'InConsultation')
  );
}

export type AppointmentAction = 'consult' | 'note' | 'video';

/**
 * The one thing to do with a booking: open the signed note once seen; join a video booking from the video
 * consultations (today, until it is over); otherwise start or continue an arrived patient's consultation.
 */
export function appointmentAction(
  appointment: Pick<WorklistAppointment, 'status' | 'visitId' | 'encounterId' | 'isTelehealth'>,
  { canWrite, isToday }: { canWrite: boolean; isToday: boolean },
): AppointmentAction | null {
  if (appointment.status === 'Completed') {
    return canWrite && appointment.encounterId ? 'note' : null;
  }
  if (appointment.isTelehealth) {
    return isToday && appointment.status !== 'NoShow' ? 'video' : null;
  }
  return canWrite && canConsult(appointment) ? 'consult' : null;
}

export interface AppointmentSummary {
  total: number;
  toCome: number;
  arrived: number;
  seen: number;
  noShow: number;
}

export function summariseAppointments(
  appointments: readonly Pick<WorklistAppointment, 'status'>[],
): AppointmentSummary {
  const count = (...statuses: AppointmentStatus[]) => appointments.filter((a) => statuses.includes(a.status)).length;
  return {
    total: appointments.length,
    toCome: count('Booked', 'Confirmed'),
    arrived: count('CheckedIn', 'InConsultation'),
    seen: count('Completed'),
    noShow: count('NoShow'),
  };
}

/** "9 appointments: 4 to come, 3 arrived, 2 seen" (parts that are zero are left out). */
export function appointmentSummaryText(summary: AppointmentSummary): string {
  if (summary.total === 0) {
    return 'No appointments';
  }
  const parts = [
    summary.toCome > 0 ? `${summary.toCome} to come` : null,
    summary.arrived > 0 ? `${summary.arrived} arrived` : null,
    summary.seen > 0 ? `${summary.seen} seen` : null,
    summary.noShow > 0 ? `${summary.noShow} did not come` : null,
  ].filter((part): part is string => part !== null);
  return `${summary.total} ${summary.total === 1 ? 'appointment' : 'appointments'}: ${parts.join(', ')}`;
}

// ---------------------------------------------------------------------------------------------------- ward round

export type RoundOrder = 'bed' | 'attention';

type RoundFacts = Pick<
  WardRoundPatient,
  'vitalsSeverity' | 'hasCriticalResults' | 'resultsToReview' | 'seenToday' | 'status'
>;

/**
 * How much a patient needs the doctor now, for "most urgent first": critical vital signs or a critical result first,
 * then abnormal vital signs, then results waiting and patients not yet seen today. Zero means nothing is pending.
 */
export function roundAttention(patient: RoundFacts): number {
  let score = 0;
  if (patient.vitalsSeverity === 'Critical') {
    score += 8;
  } else if (patient.vitalsSeverity === 'Abnormal') {
    score += 2;
  }
  if (patient.hasCriticalResults) {
    score += 8;
  } else if (patient.resultsToReview > 0) {
    score += 1;
  }
  if (!patient.seenToday) {
    score += 1;
  }
  return score;
}

/** Bed order is the server's (the bed board's walking order); "most urgent first" keeps it among equals. */
export function sortRound<T extends RoundFacts>(patients: readonly T[], order: RoundOrder): T[] {
  if (order === 'bed') {
    return [...patients];
  }
  return patients
    .map((patient, index) => ({ patient, index, score: roundAttention(patient) }))
    .sort((a, b) => b.score - a.score || a.index - b.index)
    .map((entry) => entry.patient);
}

export interface RoundSummary {
  total: number;
  notSeen: number;
  urgent: number;
  withResults: number;
}

export function summariseRound(patients: readonly RoundFacts[]): RoundSummary {
  return {
    total: patients.length,
    notSeen: patients.filter((p) => !p.seenToday).length,
    urgent: patients.filter((p) => p.vitalsSeverity === 'Critical' || p.hasCriticalResults).length,
    withResults: patients.filter((p) => p.resultsToReview > 0).length,
  };
}

/** "8 in-patients: 3 not seen today, 1 needs attention now" (parts that are zero are left out). */
export function roundSummaryText(summary: RoundSummary): string {
  if (summary.total === 0) {
    return 'No in-patients';
  }
  const parts = [
    summary.notSeen > 0 ? `${summary.notSeen} not seen today` : 'all seen today',
    summary.urgent > 0 ? `${summary.urgent} ${summary.urgent === 1 ? 'needs' : 'need'} attention now` : null,
    summary.withResults > 0 ? `${summary.withResults} with results to review` : null,
  ].filter((part): part is string => part !== null);
  return `${summary.total} ${summary.total === 1 ? 'in-patient' : 'in-patients'}: ${parts.join(', ')}`;
}

/** "Day 3", and "Admitted today" on the first day. */
export function dayOfStayLabel(dayOfStay: number): string {
  return dayOfStay <= 1 ? 'Admitted today' : `Day ${dayOfStay}`;
}
