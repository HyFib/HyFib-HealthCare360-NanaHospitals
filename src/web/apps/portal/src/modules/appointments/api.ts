import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  ConfirmPaymentRequest,
  PaymentCheckout,
  PortalAppointment,
  PortalBookAppointmentRequest,
  PortalBookingResult,
  PortalJoinWaitlistRequest,
  PortalWaitlistEntry,
} from '@/lib/types';

export type AppointmentScope = 'upcoming' | 'past';

const base = '/api/v1/portal/appointments';
const id = (value: string) => encodeURIComponent(value);

/**
 * The server returns the whole family's appointments (everyone on the verified number); every query keeps only the
 * person being viewed, and its key carries their id so one person's list can never show under another's name.
 */
export const appointmentQueries = {
  all: (patientId: string) => ['portal', patientId, 'appointments'] as const,

  list: (patientId: string, scope: AppointmentScope) =>
    queryOptions({
      queryKey: ['portal', patientId, 'appointments', scope],
      queryFn: ({ signal }) => api.get<PortalAppointment[]>(base, { query: { scope }, signal }),
      select: (rows) => rows.filter((row) => row.patientId === patientId),
    }),

  detail: (patientId: string, appointmentId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'appointments', 'detail', appointmentId],
      queryFn: ({ signal }) => api.get<PortalAppointment>(`${base}/${id(appointmentId)}`, { signal }),
    }),

  waitlist: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'appointments', 'waitlist'],
      queryFn: ({ signal }) => api.get<PortalWaitlistEntry[]>('/api/v1/portal/waitlist', { signal }),
      select: (rows) => rows.filter((row) => row.patientId === patientId),
    }),
};

export const appointmentApi = {
  book: (request: PortalBookAppointmentRequest) => api.post<PortalBookingResult>(base, request),
  cancel: (appointmentId: string, reason: string | null) => api.post<void>(`${base}/${id(appointmentId)}/cancel`, { reason }),
  reschedule: (appointmentId: string, startsAt: string, reason: string | null) =>
    api.post<PortalAppointment>(`${base}/${id(appointmentId)}/reschedule`, { startsAt, reason }),
  startPayment: (appointmentId: string) => api.post<PaymentCheckout>(`${base}/${id(appointmentId)}/payment`),
  confirmPayment: (appointmentId: string, body: ConfirmPaymentRequest) => api.post<PortalAppointment>(`${base}/${id(appointmentId)}/payment/confirm`, body),
  joinWaitlist: (request: PortalJoinWaitlistRequest) => api.post<PortalWaitlistEntry>('/api/v1/portal/waitlist', request),
  leaveWaitlist: (entryId: string) => api.post<void>(`/api/v1/portal/waitlist/${id(entryId)}/cancel`),
};
