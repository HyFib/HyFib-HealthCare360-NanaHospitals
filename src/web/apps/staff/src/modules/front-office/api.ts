import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  QueueTokenDto,
  TokenPriority,
  TokenStatus,
  VisitListItem,
  VisitStatus,
  VisitType,
} from '@/modules/patients/api';

export interface DepartmentQueueStat {
  departmentId: string;
  departmentName: string;
  waiting: number;
  served: number;
  nowServing: string | null;
}

export interface FrontOfficeDashboard {
  registrationsToday: number;
  visitsToday: number;
  openVisits: number;
  completedVisits: number;
  waitingTokens: number;
  averageWaitMinutes: number | null;
  openEnquiries: number;
  visitorsInside: number;
  departments: DepartmentQueueStat[];
  visitsByType: { type: VisitType; count: number }[];
}

export interface QueueEntry {
  tokenId: string;
  label: string;
  status: TokenStatus;
  priority: TokenPriority;
  visitId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  practitionerId: string | null;
  room: string | null;
  issuedAt: string;
  calledAt: string | null;
  waitingMinutes: number;
  chiefComplaint: string | null;
  isFollowUp: boolean;
}

export interface DisplayDepartment {
  departmentId: string;
  departmentName: string;
  nowServing: { label: string; maskedName: string; room: string | null; calledAt: string | null }[];
  upNext: string[];
  waiting: number;
}

export type EnquiryType =
  | 'General'
  | 'Appointment'
  | 'DoctorAvailability'
  | 'Billing'
  | 'Insurance'
  | 'HealthPackage'
  | 'Admission'
  | 'Complaint'
  | 'Other';
export type EnquiryChannel = 'WalkIn' | 'Phone' | 'WhatsApp' | 'Email' | 'Website';
export type EnquiryStatus = 'Open' | 'InProgress' | 'Resolved' | 'Converted';

export interface EnquiryDto {
  id: string;
  name: string;
  phone: string | null;
  type: EnquiryType;
  channel: EnquiryChannel;
  details: string;
  status: EnquiryStatus;
  response: string | null;
  patientId: string | null;
  createdAt: string;
  resolvedAt: string | null;
  version: number;
}

export interface SaveEnquiryRequest {
  name: string;
  phone: string | null;
  type: EnquiryType;
  channel: EnquiryChannel;
  details: string;
  status: EnquiryStatus;
  response: string | null;
  patientId: string | null;
  version: number | null;
}

export type VisitorPassStatus = 'Active' | 'CheckedOut' | 'Expired' | 'Revoked';

export interface VisitorPassDto {
  id: string;
  passNumber: string;
  patientId: string | null;
  patientName: string | null;
  patientUhid: string | null;
  visitorName: string;
  phone: string | null;
  relation: string | null;
  idProofType: string | null;
  idProofLast4: string | null;
  purpose: string | null;
  location: string | null;
  isAttendant: boolean;
  validFrom: string;
  validUntil: string;
  status: VisitorPassStatus;
  checkedOutAt: string | null;
}

export interface IssueVisitorPassRequest {
  patientId: string | null;
  visitorName: string;
  phone: string | null;
  relation: string | null;
  idProofType: string | null;
  idProofNumber: string | null;
  purpose: string | null;
  location: string | null;
  isAttendant: boolean;
  validHours: number;
}

export interface VisitFilters {
  from?: string;
  to?: string;
  type?: VisitType;
  status?: VisitStatus;
  departmentId?: string;
  search?: string;
  page?: number;
}

export const frontOfficeQueries = {
  dashboard: () =>
    queryOptions({
      queryKey: ['front-office', 'dashboard'],
      queryFn: ({ signal }) => api.get<FrontOfficeDashboard>('/api/v1/front-office/dashboard', { signal }),
      refetchInterval: 60_000,
    }),
  visits: (filters: VisitFilters) =>
    queryOptions({
      queryKey: ['visits', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<VisitListItem>>('/api/v1/visits', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  queue: (departmentId: string, practitionerId?: string | null, includeClosed?: boolean) =>
    queryOptions({
      queryKey: ['front-office', 'queue', departmentId, practitionerId ?? null, includeClosed ?? false],
      queryFn: ({ signal }) =>
        api.get<QueueEntry[]>('/api/v1/queue', { query: { departmentId, practitionerId, includeClosed }, signal }),
      refetchInterval: 30_000,
    }),
  display: (departmentIds: string[]) =>
    queryOptions({
      queryKey: ['front-office', 'display', departmentIds],
      queryFn: ({ signal }) =>
        api.get<DisplayDepartment[]>('/api/v1/queue/display', {
          query: { departmentIds: departmentIds.join(',') },
          signal,
        }),
      refetchInterval: 20_000,
    }),
  enquiries: (status: EnquiryStatus | undefined, page: number, search?: string) =>
    queryOptions({
      queryKey: ['front-office', 'enquiries', status ?? 'all', page, search ?? ''],
      queryFn: ({ signal }) =>
        api.get<PagedResult<EnquiryDto>>('/api/v1/front-office/enquiries', {
          query: { status, page, pageSize: 25, search },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  visitorPasses: (activeOnly: boolean, page: number, search?: string) =>
    queryOptions({
      queryKey: ['front-office', 'visitor-passes', activeOnly, page, search ?? ''],
      queryFn: ({ signal }) =>
        api.get<PagedResult<VisitorPassDto>>('/api/v1/front-office/visitor-passes', {
          query: { activeOnly, page, pageSize: 25, search },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
};

export const queueApi = {
  callNext: (departmentId: string, practitionerId: string | null, room: string | null) =>
    api.post<QueueTokenDto | null>('/api/v1/queue/call-next', { departmentId, practitionerId, room }),
  call: (tokenId: string, room: string | null) => api.post(`/api/v1/queue/${tokenId}/call`, { room }),
  start: (tokenId: string) => api.post(`/api/v1/queue/${tokenId}/start`),
  complete: (tokenId: string) => api.post(`/api/v1/queue/${tokenId}/complete`),
  skip: (tokenId: string) => api.post(`/api/v1/queue/${tokenId}/skip`),
  requeue: (tokenId: string) => api.post(`/api/v1/queue/${tokenId}/requeue`),
};

/** Cancelling a visit's reason is required, up to 300 characters. */
export const CANCEL_VISIT_REASON_MAX = 300;

/**
 * Ending a visit from the front desk needs `visits.manage`. Cancelling (a patient checked in by mistake, or who left)
 * also cancels its queue token and withdraws Billing's own unbilled charges; closing finishes the token. Both answer 422
 * `visit.closed` / `visit.cancelled` when the visit has already ended.
 */
export const visitApi = {
  cancel: (visitId: string, reason: string) => api.post<void>(`/api/v1/visits/${visitId}/cancel`, { reason }),
  complete: (visitId: string) => api.post<void>(`/api/v1/visits/${visitId}/complete`),
};
