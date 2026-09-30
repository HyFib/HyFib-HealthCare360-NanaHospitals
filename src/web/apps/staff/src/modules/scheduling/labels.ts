import type { StatusTone } from '@hc360/ui';
import type {
  AppointmentChannel,
  AppointmentPaymentStatus,
  AppointmentStatus,
  AppointmentVisitType,
  CancellationReason,
  ConsultationMode,
  PreferredSession,
  ScheduleExceptionType,
  WaitlistStatus,
  Weekday,
} from './api';

/** Plain words a front-desk user understands, and the badge colour for each appointment status. */
export const appointmentStatusText: Record<AppointmentStatus, { label: string; tone: StatusTone }> = {
  Booked: { label: 'Booked', tone: 'warning' },
  Confirmed: { label: 'Confirmed', tone: 'brand' },
  CheckedIn: { label: 'Checked in', tone: 'brand' },
  InConsultation: { label: 'With doctor', tone: 'brand' },
  Completed: { label: 'Completed', tone: 'success' },
  Cancelled: { label: 'Cancelled', tone: 'danger' },
  NoShow: { label: 'No-show', tone: 'danger' },
  Rescheduled: { label: 'Rescheduled', tone: 'informative' },
};

export const channelLabel: Record<AppointmentChannel, string> = {
  WalkIn: 'At the desk',
  Phone: 'Phone call',
  Online: 'Website',
  Portal: 'Patient portal',
  Kiosk: 'Kiosk',
  WhatsApp: 'WhatsApp',
};

export const visitTypeLabel: Record<AppointmentVisitType, string> = { New: 'New visit', FollowUp: 'Follow-up' };

export const paymentLabel: Record<AppointmentPaymentStatus, string> = {
  Unpaid: 'Not paid',
  PaidOnline: 'Paid online',
  PayAtHospital: 'Pay at hospital',
};

export const cancellationReasonLabel: Record<CancellationReason, string> = {
  PatientRequest: "Patient's request",
  DoctorUnavailable: 'Doctor unavailable',
  HospitalRequest: 'Hospital request',
  Duplicate: 'Duplicate booking',
  PaymentNotReceived: 'Payment not received',
  Other: 'Other reason',
};

export const modeLabel: Record<ConsultationMode, string> = {
  InPerson: 'In person',
  Telehealth: 'Video only',
  Both: 'In person and video',
};

export const weekdayShort: Record<Weekday, string> = {
  Monday: 'Mon',
  Tuesday: 'Tue',
  Wednesday: 'Wed',
  Thursday: 'Thu',
  Friday: 'Fri',
  Saturday: 'Sat',
  Sunday: 'Sun',
};

export const exceptionTypeText: Record<
  ScheduleExceptionType,
  { label: string; tone: StatusTone; description: string }
> = {
  Leave: { label: 'Leave', tone: 'danger', description: 'The doctor is away. No appointments on these days.' },
  Block: {
    label: 'Blocked time',
    tone: 'warning',
    description: 'Time taken out of OPD, such as a surgery list or a meeting.',
  },
  ExtraSession: {
    label: 'Extra session',
    tone: 'success',
    description: 'An additional OPD session, such as a camp or a Sunday clinic.',
  },
};

export const waitlistStatusText: Record<WaitlistStatus, { label: string; tone: StatusTone }> = {
  Waiting: { label: 'Waiting', tone: 'warning' },
  Offered: { label: 'Slot offered', tone: 'brand' },
  Booked: { label: 'Booked', tone: 'success' },
  Cancelled: { label: 'Cancelled', tone: 'informative' },
  Expired: { label: 'Expired', tone: 'informative' },
};

export const preferredSessionLabel: Record<PreferredSession, string> = {
  Any: 'Any time',
  Morning: 'Morning (before 12 pm)',
  Afternoon: 'Afternoon (12–4 pm)',
  Evening: 'Evening (after 4 pm)',
};
