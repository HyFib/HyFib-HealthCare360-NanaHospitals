import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { Laterality } from '@/modules/ot/api';
import type { ChargeOutcome, ChargeStatus, OrderPriority } from '@/modules/ot/shared/types';
import type { PatientSummary } from '@/modules/patients/api';

// ---------------------------------------------------------------- enums (JSON strings)

export const REFERRAL_STATUSES = ['New', 'Assessed', 'InTreatment', 'Completed', 'Cancelled'] as const;
export type PhysioReferralStatus = (typeof REFERRAL_STATUSES)[number];

export const MODALITIES = [
  'IFT',
  'TENS',
  'UST',
  'SWD',
  'HotPack',
  'ColdPack',
  'Traction',
  'Exercises',
  'Mobilisation',
  'ChestPhysiotherapy',
  'GaitTraining',
  'WaxBath',
  'Laser',
  'Other',
] as const;
export type PhysioModality = (typeof MODALITIES)[number];

export const ASSESSMENT_TYPES = ['Initial', 'Review', 'Discharge'] as const;
export type PhysioAssessmentType = (typeof ASSESSMENT_TYPES)[number];

export const PHYSIO_SESSION_STATUSES = ['Scheduled', 'Attended', 'Missed', 'Cancelled'] as const;
export type PhysioSessionStatus = (typeof PHYSIO_SESSION_STATUSES)[number];

export const ORDER_PRIORITIES = ['Routine', 'Urgent', 'Stat'] as const satisfies readonly OrderPriority[];

// ---------------------------------------------------------------- DTOs (mirror Features/Physio/PhysioContracts.cs)

export interface RangeOfMotion {
  joint: string;
  movement: string;
  side: Laterality;
  activeDegrees: number | null;
  passiveDegrees: number | null;
}

export interface MuscleStrength {
  muscleGroup: string;
  side: Laterality;
  mrcGrade: number;
}

export interface CreatePhysioReferralRequest {
  patientId: string;
  visitId: string | null;
  admissionId: string | null;
  referredByPractitionerId: string | null;
  priority: OrderPriority;
  reason: string;
  sessionsOrdered: number | null;
}

export interface RecordPhysioAssessmentRequest {
  type: PhysioAssessmentType;
  chiefComplaint: string;
  painScore: number | null;
  painLocation: string | null;
  rangeOfMotion: RangeOfMotion[] | null;
  strength: MuscleStrength[] | null;
  functionalStatus: string | null;
  goals: string | null;
  notes: string | null;
}

export interface SaveTreatmentPlanRequest {
  modalities: PhysioModality[];
  exerciseProgramme: string | null;
  sessionsPlanned: number | null;
  sessionsPerWeek: number;
  sessionMinutes: number;
  startDate: string;
  precautions: string | null;
  goals: string | null;
  version: number | null;
}

export interface AttendPhysioSessionRequest {
  modalities: PhysioModality[];
  progressNote: string;
  painBefore: number | null;
  painAfter: number | null;
  durationMinutes: number | null;
}

export interface PhysioReferralListItem {
  id: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  serviceName: string | null;
  reason: string | null;
  sessionsOrdered: number | null;
  priority: OrderPriority;
  status: PhysioReferralStatus;
  referredAt: string;
  referredByPractitionerId: string | null;
  referredByName: string | null;
  assignedTherapistName: string | null;
  isInpatient: boolean;
}

export interface PhysioAssessmentDto {
  id: string;
  type: PhysioAssessmentType;
  assessedAt: string;
  assessedByName: string | null;
  chiefComplaint: string;
  painScore: number | null;
  painLocation: string | null;
  rangeOfMotion: RangeOfMotion[];
  strength: MuscleStrength[];
  functionalStatus: string | null;
  goals: string | null;
  notes: string | null;
}

export interface TreatmentPlanDto {
  id: string;
  modalities: PhysioModality[];
  exerciseProgramme: string | null;
  sessionsPlanned: number;
  sessionsPerWeek: number;
  sessionMinutes: number;
  startDate: string;
  precautions: string | null;
  goals: string | null;
  revision: number;
  version: number;
}

export interface PhysioSessionDto {
  id: string;
  referralId: string;
  patientId: string;
  sessionNumber: number;
  scheduledAt: string;
  status: PhysioSessionStatus;
  modalitiesGiven: PhysioModality[];
  progressNote: string | null;
  painBefore: number | null;
  painAfter: number | null;
  durationMinutes: number | null;
  attendedAt: string | null;
  therapistName: string | null;
  missedReason: string | null;
  chargeStatus: ChargeStatus;
  chargeError: string | null;
  version: number;
}

export interface PhysioReferralDetail {
  id: string;
  patient: PatientSummary | null;
  patientId: string;
  visitId: string | null;
  admissionId: string | null;
  orderItemId: string | null;
  orderNumber: string | null;
  serviceName: string | null;
  sessionsOrdered: number | null;
  referredByPractitionerId: string | null;
  referredByName: string | null;
  priority: OrderPriority;
  reason: string | null;
  referredAt: string;
  status: PhysioReferralStatus;
  assignedTherapistName: string | null;
  dischargeSummary: string | null;
  closedAt: string | null;
  cancelReason: string | null;
  assessments: PhysioAssessmentDto[];
  plan: TreatmentPlanDto | null;
  sessions: PhysioSessionDto[];
  version: number;
}

/** Row of the physiotherapy day list. */
export interface PhysioWorklistItem {
  sessionId: string;
  referralId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  sessionNumber: number;
  sessionsPlanned: number | null;
  scheduledAt: string;
  status: PhysioSessionStatus;
  therapistName: string | null;
  version: number;
}

// ---------------------------------------------------------------- queries

export interface ReferralFilters {
  status?: PhysioReferralStatus;
  closed?: boolean;
  search?: string;
  page?: number;
}

export const physioKeys = {
  all: ['physio'] as const,
  referralLists: ['physio', 'referrals'] as const,
  referrals: (filters: ReferralFilters) => ['physio', 'referrals', filters] as const,
  referral: (id: string) => ['physio', 'referral', id] as const,
  worklists: ['physio', 'worklist'] as const,
  worklist: (date: string, status?: PhysioSessionStatus) => ['physio', 'worklist', date, status ?? 'all'] as const,
};

export const physioQueries = {
  referrals: (filters: ReferralFilters) =>
    queryOptions({
      queryKey: physioKeys.referrals(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<PhysioReferralListItem>>('/api/v1/physio/referrals', {
          query: {
            status: filters.status,
            openOnly: filters.closed ? false : undefined,
            search: filters.search,
            page: filters.page,
            pageSize: 25,
          },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  referral: (id: string) =>
    queryOptions({
      queryKey: physioKeys.referral(id),
      queryFn: ({ signal }) => api.get<PhysioReferralDetail>(`/api/v1/physio/referrals/${id}`, { signal }),
    }),
  worklist: (date: string, status?: PhysioSessionStatus) =>
    queryOptions({
      queryKey: physioKeys.worklist(date, status),
      queryFn: ({ signal }) =>
        api.get<PhysioWorklistItem[]>('/api/v1/physio/sessions', { query: { date, status }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
};

export const physioApi = {
  createReferral: (request: CreatePhysioReferralRequest) =>
    api.post<{ id: string }>('/api/v1/physio/referrals', request),
  cancelReferral: (id: string, reason: string) => api.post<void>(`/api/v1/physio/referrals/${id}/cancel`, { reason }),
  completeReferral: (id: string, summary: string) =>
    api.post<void>(`/api/v1/physio/referrals/${id}/complete`, { reason: summary }),
  recordAssessment: (id: string, request: RecordPhysioAssessmentRequest) =>
    api.post<{ id: string }>(`/api/v1/physio/referrals/${id}/assessments`, request),
  savePlan: (id: string, request: SaveTreatmentPlanRequest) =>
    api.put<{ id: string }>(`/api/v1/physio/referrals/${id}/plan`, request),
  scheduleSessions: (id: string, firstSessionAt: string, count: number, intervalDays: number | null) =>
    api.post<string[]>(`/api/v1/physio/referrals/${id}/sessions`, { firstSessionAt, count, intervalDays }),
  /** `version`: the session as it was shown; one moved meanwhile is not moved again over it (REL-04). */
  reschedule: (sessionId: string, scheduledAt: string, version: number) =>
    api.put<void>(`/api/v1/physio/sessions/${sessionId}`, { scheduledAt, version }),
  attend: (sessionId: string, request: AttendPhysioSessionRequest) =>
    api.post<void>(`/api/v1/physio/sessions/${sessionId}/attend`, request),
  close: (sessionId: string, reason: string, missed: boolean) =>
    api.post<void>(`/api/v1/physio/sessions/${sessionId}/close`, { reason, missed }),
  retryCharges: (sessionId: string) => api.post<ChargeOutcome>(`/api/v1/physio/sessions/${sessionId}/charges/retry`),
};
