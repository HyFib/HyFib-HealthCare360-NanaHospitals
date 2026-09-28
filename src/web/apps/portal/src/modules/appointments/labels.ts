import type { Tone } from '@/components/ListRow';
import type { MessageKey } from '@/i18n';
import type { AppointmentPaymentStatus, AppointmentStatus, AppointmentVisitType, PreferredSession } from '@/lib/types';

export const APPOINTMENT_STATUS: Record<AppointmentStatus, { key: MessageKey; tone: Tone }> = {
  Booked: { key: 'appointment.status.Booked', tone: 'brand' },
  Confirmed: { key: 'appointment.status.Confirmed', tone: 'success' },
  CheckedIn: { key: 'appointment.status.CheckedIn', tone: 'brand' },
  InConsultation: { key: 'appointment.status.InConsultation', tone: 'brand' },
  Completed: { key: 'appointment.status.Completed', tone: 'neutral' },
  Cancelled: { key: 'appointment.status.Cancelled', tone: 'danger' },
  NoShow: { key: 'appointment.status.NoShow', tone: 'warning' },
  Rescheduled: { key: 'appointment.status.Rescheduled', tone: 'neutral' },
};

export const PAYMENT_STATUS: Record<AppointmentPaymentStatus, { key: MessageKey; tone: Tone }> = {
  Unpaid: { key: 'appointment.payment.Unpaid', tone: 'warning' },
  PaidOnline: { key: 'appointment.payment.PaidOnline', tone: 'success' },
  PayAtHospital: { key: 'appointment.payment.PayAtHospital', tone: 'neutral' },
};

export const VISIT_TYPE: Record<AppointmentVisitType, MessageKey> = {
  New: 'appointment.visit.New',
  FollowUp: 'appointment.visit.FollowUp',
};

export const PREFERRED_SESSION: Record<PreferredSession, MessageKey> = {
  Any: 'waitlist.session.Any',
  Morning: 'waitlist.session.Morning',
  Afternoon: 'waitlist.session.Afternoon',
  Evening: 'waitlist.session.Evening',
};

/** Statuses in which the appointment is still ahead (not cancelled, missed, finished or moved). */
export function isOpenAppointment(status: AppointmentStatus): boolean {
  return status === 'Booked' || status === 'Confirmed' || status === 'CheckedIn' || status === 'InConsultation';
}
