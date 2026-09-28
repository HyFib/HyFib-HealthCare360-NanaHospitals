import type { Gender, PagedResult, PayerType } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { TokenPriority } from '@/modules/patients/api';

// ------------------------------------------------------------------ enums (strings, as the server sends them)

export const APPOINTMENT_STATUSES = ['Booked', 'Confirmed', 'CheckedIn', 'InConsultation', 'Completed', 'Cancelled', 'NoShow', 'Rescheduled'] as const;
export type AppointmentStatus = (typeof APPOINTMENT_STATUSES)[number];

export const APPOINTMENT_CHANNELS = ['WalkIn', 'Phone', 'Online', 'Portal', 'Kiosk', 'WhatsApp'] as const;
export type AppointmentChannel = (typeof APPOINTMENT_CHANNELS)[number];

/** Channels the staff app may book with (portal bookings are made by patients). */
export const STAFF_CHANNELS = ['WalkIn', 'Phone', 'WhatsApp', 'Online', 'Kiosk'] as const satisfies readonly AppointmentChannel[];

export type AppointmentVisitType = 'New' | 'FollowUp';
export type AppointmentPaymentStatus = 'Unpaid' | 'PaidOnline' | 'PayAtHospital';

export const CANCELLATION_REASONS = ['PatientRequest', 'DoctorUnavailable', 'HospitalRequest', 'Duplicate', 'PaymentNotReceived', 'Other'] as const;
export type CancellationReason = (typeof CANCELLATION_REASONS)[number];
export type CancellationSource = 'Staff' | 'Patient' | 'System';

export type ConsultationMode = 'InPerson' | 'Telehealth' | 'Both';

export const WEEKDAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;
export type Weekday = (typeof WEEKDAYS)[number];

export const EXCEPTION_TYPES = ['Leave', 'Block', 'ExtraSession'] as const;
export type ScheduleExceptionType = (typeof EXCEPTION_TYPES)[number];

export const WAITLIST_STATUSES = ['Waiting', 'Offered', 'Booked', 'Cancelled', 'Expired'] as const;
export type WaitlistStatus = (typeof WAITLIST_STATUSES)[number];

export const PREFERRED_SESSIONS = ['Any', 'Morning', 'Afternoon', 'Evening'] as const;
export type PreferredSession = (typeof PREFERRED_SESSIONS)[number];

// ------------------------------------------------------------------ appointments

export interface AppointmentListItem {
  id: string;
  number: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  phoneMasked: string | null;
  practitionerId: string;
  practitionerName: string;
  departmentId: string;
  departmentName: string | null;
  startsAt: string;
  endsAt: string;
  seatNumber: number;
  status: AppointmentStatus;
  channel: AppointmentChannel;
  visitType: AppointmentVisitType;
  isTelehealth: boolean;
  paymentStatus: AppointmentPaymentStatus;
  feeAmount: number;
  reason: string | null;
  room: string | null;
  visitId: string | null;
  bookedAt: string;
}

export interface AppointmentHistoryItem {
  id: string;
  number: string;
  practitionerId: string;
  startsAt: string;
  status: AppointmentStatus;
  bookedAt: string;
  note: string | null;
}

export interface AppointmentDetail {
  id: string;
  number: string;
  facilityId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  phone: string | null;
  practitionerId: string;
  practitionerName: string;
  departmentId: string;
  departmentName: string | null;
  scheduleId: string | null;
  startsAt: string;
  endsAt: string;
  seatNumber: number;
  status: AppointmentStatus;
  channel: AppointmentChannel;
  visitType: AppointmentVisitType;
  isTelehealth: boolean;
  room: string | null;
  reason: string | null;
  notes: string | null;
  bookedAt: string;
  confirmedAt: string | null;
  feeAmount: number;
  paymentStatus: AppointmentPaymentStatus;
  paymentProvider: string | null;
  gatewayOrderId: string | null;
  gatewayPaymentId: string | null;
  amountPaid: number | null;
  paidAt: string | null;
  visitId: string | null;
  checkedInAt: string | null;
  completedAt: string | null;
  noShowAt: string | null;
  cancelReasonCode: CancellationReason | null;
  cancelReason: string | null;
  cancelledBy: CancellationSource | null;
  cancelledAt: string | null;
  rescheduledFromId: string | null;
  rescheduledToId: string | null;
  waitlistEntryId: string | null;
  reminder24hSentAt: string | null;
  reminder2hSentAt: string | null;
  history: AppointmentHistoryItem[];
  version: number;
}

/**
 * The fields every appointment view shares (list rows and details both satisfy it), so dialogs, actions and the
 * printed slip work from whichever one the screen has.
 */
export interface AppointmentRef {
  id: string;
  number: string;
  patientId: string;
  uhid: string;
  patientName: string;
  practitionerId: string;
  practitionerName: string;
  departmentId: string;
  departmentName: string | null;
  startsAt: string;
  endsAt: string;
  status: AppointmentStatus;
  channel: AppointmentChannel;
  visitType: AppointmentVisitType;
  isTelehealth: boolean;
  paymentStatus: AppointmentPaymentStatus;
  feeAmount: number;
  reason: string | null;
  room: string | null;
  visitId: string | null;
}

export interface CalendarAppointment {
  id: string;
  number: string;
  patientId: string;
  patientName: string;
  uhid: string;
  practitionerId: string;
  departmentId: string;
  startsAt: string;
  endsAt: string;
  status: AppointmentStatus;
  visitType: AppointmentVisitType;
  channel: AppointmentChannel;
  isTelehealth: boolean;
  paymentStatus: AppointmentPaymentStatus;
}

export interface PractitionerDayLoad {
  practitionerId: string;
  practitionerName: string;
  departmentId: string | null;
  capacity: number;
  booked: number;
  checkedIn: number;
  completed: number;
  noShow: number;
  cancelled: number;
  freeSlots: number;
}

export interface AppointmentDaySummary {
  date: string;
  total: number;
  booked: number;
  confirmed: number;
  checkedIn: number;
  inConsultation: number;
  completed: number;
  cancelled: number;
  noShow: number;
  telehealth: number;
  online: number;
  practitioners: PractitionerDayLoad[];
}

export interface BookAppointmentRequest {
  patientId: string;
  practitionerId: string;
  startsAt: string;
  channel: AppointmentChannel;
  visitType: AppointmentVisitType;
  isTelehealth: boolean;
  reason: string | null;
  notes: string | null;
  overbook: boolean;
  notifyPatient: boolean;
  waitlistEntryId: string | null;
}

export interface RescheduleAppointmentRequest {
  startsAt: string;
  practitionerId: string | null;
  isTelehealth: boolean | null;
  reason: string | null;
  overbook: boolean;
  notifyPatient: boolean;
}

export interface CancelAppointmentRequest {
  reasonCode: CancellationReason;
  reason: string | null;
  notifyPatient: boolean;
}

export interface CheckInAppointmentRequest {
  payerType: PayerType;
  payerId: string | null;
  priority: TokenPriority;
  chiefComplaint: string | null;
  issueToken: boolean;
}

export interface CheckInResult {
  appointmentId: string;
  visitId: string;
  visitNumber: string;
  tokenLabel: string | null;
  linkedExistingVisit: boolean;
}

// ------------------------------------------------------------------ slots

export interface SlotDto {
  startsAt: string;
  endsAt: string;
  /** IST calendar date "yyyy-MM-dd". */
  date: string;
  /** IST wall clock "HH:mm:ss". */
  localStart: string;
  localEnd: string;
  capacity: number;
  booked: number;
  available: number;
  mode: ConsultationMode;
  facilityId: string;
  departmentId: string;
  room: string | null;
}

export interface PractitionerAvailability {
  practitionerId: string;
  practitionerName: string;
  departmentId: string | null;
  departmentName: string | null;
  offersTelehealth: boolean;
  freeSlots: number;
  slots: SlotDto[];
}

// ------------------------------------------------------------------ schedules and exceptions

export interface ScheduleDto {
  id: string;
  facilityId: string;
  practitionerId: string;
  practitionerName: string;
  departmentId: string;
  departmentName: string | null;
  weekday: Weekday;
  startTime: string;
  endTime: string;
  slotMinutes: number;
  capacityPerSlot: number;
  slotsPerSession: number;
  mode: ConsultationMode;
  room: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
  notes: string | null;
  version: number;
}

export interface CreateSchedulesRequest {
  practitionerId: string;
  departmentId: string;
  weekdays: Weekday[];
  startTime: string;
  endTime: string;
  slotMinutes: number;
  capacityPerSlot: number;
  mode: ConsultationMode;
  room: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  notes: string | null;
}

export interface UpdateScheduleRequest {
  departmentId: string;
  weekday: Weekday;
  startTime: string;
  endTime: string;
  slotMinutes: number;
  capacityPerSlot: number;
  mode: ConsultationMode;
  room: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
  notes: string | null;
  version: number;
}

export interface SchedulesCreated {
  ids: string[];
}

/** A booking that a schedule change strands (inside new leave/blocks or outside an edited session). */
export interface AffectedAppointment {
  id: string;
  number: string;
  patientId: string;
  patientName: string;
  phoneMasked: string | null;
  startsAt: string;
  status: AppointmentStatus;
}

export interface ScheduleSaved {
  id: string;
  affectedAppointments: AffectedAppointment[];
}

export interface ScheduleExceptionDto {
  id: string;
  practitionerId: string;
  practitionerName: string;
  facilityId: string | null;
  type: ScheduleExceptionType;
  fromDate: string;
  toDate: string;
  isAllDay: boolean;
  startTime: string | null;
  endTime: string | null;
  reason: string;
  departmentId: string | null;
  slotMinutes: number | null;
  capacityPerSlot: number | null;
  mode: ConsultationMode | null;
  room: string | null;
  version: number;
}

export interface SaveScheduleExceptionRequest {
  practitionerId: string;
  type: ScheduleExceptionType;
  fromDate: string;
  toDate: string;
  startTime: string | null;
  endTime: string | null;
  reason: string;
  allFacilities: boolean;
  departmentId: string | null;
  slotMinutes: number | null;
  capacityPerSlot: number | null;
  mode: ConsultationMode | null;
  room: string | null;
  version: number | null;
}

// ------------------------------------------------------------------ waitlist

export interface WaitlistEntryDto {
  id: string;
  patientId: string;
  uhid: string;
  patientName: string;
  phoneMasked: string | null;
  practitionerId: string;
  practitionerName: string;
  departmentId: string | null;
  preferredFrom: string;
  preferredTo: string;
  preferredSession: PreferredSession;
  isTelehealth: boolean;
  channel: AppointmentChannel;
  notes: string | null;
  status: WaitlistStatus;
  createdAt: string;
  offeredAt: string | null;
  offeredSlotStartsAt: string | null;
  offerCount: number;
  appointmentId: string | null;
}

export interface AddToWaitlistRequest {
  patientId: string;
  practitionerId: string;
  preferredFrom: string;
  preferredTo: string;
  preferredSession: PreferredSession;
  isTelehealth: boolean;
  channel: AppointmentChannel;
  notes: string | null;
}

export interface BookFromWaitlistRequest {
  startsAt: string;
  visitType: AppointmentVisitType;
  reason: string | null;
  overbook: boolean;
}

// ------------------------------------------------------------------ queries

export interface AppointmentFilters {
  date?: string;
  departmentId?: string;
  practitionerId?: string;
  status?: AppointmentStatus;
  q?: string;
  page?: number;
}

export const APPOINTMENTS_PAGE_SIZE = 50;

/** Every scheduling query key starts with 'scheduling', so one invalidation refreshes the module after a change. */
export const schedulingKeys = {
  all: ['scheduling'] as const,
  appointments: ['scheduling', 'appointments'] as const,
  appointment: (id: string) => ['scheduling', 'appointment', id] as const,
  summary: ['scheduling', 'summary'] as const,
  slots: ['scheduling', 'slots'] as const,
  waitlist: ['scheduling', 'waitlist'] as const,
};

const BASE = '/api/v1/scheduling';

export const schedulingQueries = {
  appointments: (filters: AppointmentFilters & { from?: string; to?: string; patientId?: string; pageSize?: number }) =>
    queryOptions({
      queryKey: [...schedulingKeys.appointments, filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<AppointmentListItem>>(`${BASE}/appointments`, {
          query: {
            from: filters.from ?? filters.date,
            to: filters.to ?? filters.date,
            departmentId: filters.departmentId,
            practitionerId: filters.practitionerId,
            patientId: filters.patientId,
            status: filters.status,
            search: filters.q,
            page: filters.page,
            pageSize: filters.pageSize ?? APPOINTMENTS_PAGE_SIZE,
            sort: 'startsAt',
          },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  appointment: (id: string) =>
    queryOptions({
      queryKey: schedulingKeys.appointment(id),
      queryFn: ({ signal }) => api.get<AppointmentDetail>(`${BASE}/appointments/${id}`, { signal }),
    }),
  summary: (date: string, departmentId?: string | null) =>
    queryOptions({
      queryKey: [...schedulingKeys.summary, date, departmentId ?? 'all'],
      queryFn: ({ signal }) => api.get<AppointmentDaySummary>(`${BASE}/appointments/summary`, { query: { date, departmentId }, signal }),
      refetchInterval: 120_000,
    }),
  calendar: (params: { from: string; to: string; practitionerId?: string; departmentId?: string }) =>
    queryOptions({
      queryKey: ['scheduling', 'calendar', params],
      queryFn: ({ signal }) => api.get<CalendarAppointment[]>(`${BASE}/appointments/calendar`, { query: { ...params }, signal }),
      placeholderData: keepPreviousData,
    }),
  slots: (params: { from: string; to: string; practitionerId?: string | null; departmentId?: string | null }) =>
    queryOptions({
      queryKey: [...schedulingKeys.slots, params],
      queryFn: ({ signal }) =>
        api.get<PractitionerAvailability[]>(`${BASE}/slots`, {
          query: { from: params.from, to: params.to, practitionerId: params.practitionerId, departmentId: params.departmentId, includeFull: true },
          signal,
        }),
      staleTime: 15_000,
    }),
  schedules: (params: { practitionerId?: string; departmentId?: string; activeOnly?: boolean }) =>
    queryOptions({
      queryKey: ['scheduling', 'schedules', params],
      queryFn: ({ signal }) => api.get<ScheduleDto[]>(`${BASE}/schedules`, { query: { ...params }, signal }),
    }),
  exceptions: (params: { practitionerId?: string | null; from: string; to: string }) =>
    queryOptions({
      queryKey: ['scheduling', 'exceptions', params],
      queryFn: ({ signal }) =>
        api.get<ScheduleExceptionDto[]>(`${BASE}/exceptions`, { query: { practitionerId: params.practitionerId, from: params.from, to: params.to }, signal }),
      staleTime: 60_000,
    }),
  waitlist: (params: { practitionerId?: string; departmentId?: string; status?: WaitlistStatus; page?: number }) =>
    queryOptions({
      queryKey: [...schedulingKeys.waitlist, params],
      queryFn: ({ signal }) =>
        api.get<PagedResult<WaitlistEntryDto>>(`${BASE}/waitlist`, { query: { ...params, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
};

// ------------------------------------------------------------------ commands

export const schedulingApi = {
  book: (request: BookAppointmentRequest) => api.post<AppointmentDetail>(`${BASE}/appointments`, request),
  reschedule: (id: string, request: RescheduleAppointmentRequest) => api.post<AppointmentDetail>(`${BASE}/appointments/${id}/reschedule`, request),
  cancel: (id: string, request: CancelAppointmentRequest) => api.post<AppointmentDetail>(`${BASE}/appointments/${id}/cancel`, request),
  confirm: (id: string) => api.post<AppointmentDetail>(`${BASE}/appointments/${id}/confirm`),
  noShow: (id: string) => api.post<AppointmentDetail>(`${BASE}/appointments/${id}/no-show`),
  checkIn: (id: string, request: CheckInAppointmentRequest) => api.post<CheckInResult>(`${BASE}/appointments/${id}/check-in`, request),

  createSchedules: (request: CreateSchedulesRequest) => api.post<SchedulesCreated>(`${BASE}/schedules`, request),
  updateSchedule: (id: string, request: UpdateScheduleRequest) => api.put<ScheduleSaved>(`${BASE}/schedules/${id}`, request),
  deleteSchedule: (id: string) => api.delete<ScheduleSaved>(`${BASE}/schedules/${id}`),
  saveException: (id: string | null, request: SaveScheduleExceptionRequest) =>
    id ? api.put<ScheduleSaved>(`${BASE}/exceptions/${id}`, request) : api.post<ScheduleSaved>(`${BASE}/exceptions`, request),
  deleteException: (id: string) => api.delete<void>(`${BASE}/exceptions/${id}`),

  addToWaitlist: (request: AddToWaitlistRequest) => api.post<WaitlistEntryDto>(`${BASE}/waitlist`, request),
  cancelWaitlist: (id: string) => api.post<void>(`${BASE}/waitlist/${id}/cancel`),
  bookFromWaitlist: (id: string, request: BookFromWaitlistRequest) => api.post<AppointmentDetail>(`${BASE}/waitlist/${id}/book`, request),
};
