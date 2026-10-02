import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { PatientCategory } from '@/modules/patients/api';
import type {
  CampaignChannel,
  CampaignStatus,
  FollowUpOutcome,
  FollowUpPriority,
  RecordedFollowUpOutcome,
  FollowUpSource,
  FollowUpStatus,
  FollowUpType,
  LeadActivityType,
  LeadSource,
  LeadStatus,
} from './labels';

/** A labelled count for breakdown charts (the server's CountByLabel). */
export interface CountByLabel {
  label: string;
  count: number;
}

// ------------------------------------------------------------------ leads

export interface LeadListItem {
  id: string;
  leadNumber: string;
  name: string;
  phoneMasked: string | null;
  source: LeadSource;
  status: LeadStatus;
  interestPackage: string | null;
  interestDepartmentId: string | null;
  ownerUserId: string | null;
  nextFollowUpAt: string | null;
  lastContactedAt: string | null;
  createdAt: string;
  isFollowUpDue: boolean;
}

export interface LeadActivityDto {
  id: string;
  type: LeadActivityType;
  occurredAt: string;
  outcome: string | null;
  notes: string | null;
  performedByUserId: string | null;
}

export interface LeadDetail {
  id: string;
  leadNumber: string;
  name: string;
  phone: string | null;
  email: string | null;
  gender: Gender | null;
  ageYears: number | null;
  city: string | null;
  source: LeadSource;
  sourceDetail: string | null;
  campaignId: string | null;
  referringDoctorId: string | null;
  interestDepartmentId: string | null;
  interestPackage: string | null;
  notes: string | null;
  status: LeadStatus;
  lostReason: string | null;
  ownerUserId: string | null;
  nextFollowUpAt: string | null;
  lastContactedAt: string | null;
  convertedPatientId: string | null;
  convertedAppointmentId: string | null;
  convertedAt: string | null;
  activities: LeadActivityDto[];
  version: number;
}

export interface SaveLeadRequest {
  name: string;
  phone: string | null;
  email: string | null;
  gender: Gender | null;
  ageYears: number | null;
  city: string | null;
  source: LeadSource;
  sourceDetail: string | null;
  campaignId: string | null;
  referringDoctorId: string | null;
  interestDepartmentId: string | null;
  interestPackage: string | null;
  notes: string | null;
  ownerUserId: string | null;
  nextFollowUpAt: string | null;
  confirmedNotDuplicate: boolean;
  version: number | null;
}

export interface LeadCreated {
  id: string;
  leadNumber: string;
  existingPatientId: string | null;
}

export interface AddLeadActivityRequest {
  type: LeadActivityType;
  occurredAt: string | null;
  outcome: string | null;
  notes: string | null;
  nextFollowUpAt: string | null;
}

export interface ConvertLeadRequest {
  patientId: string | null;
  registerPatient: {
    gender: Gender;
    dateOfBirth: string | null;
    ageYears: number | null;
    confirmedNotDuplicate: boolean;
  } | null;
  appointmentId: string | null;
}

export interface LeadConversion {
  leadId: string;
  patientId: string | null;
  uhid: string | null;
  appointmentId: string | null;
}

export interface LeadFilters {
  status?: LeadStatus;
  source?: LeadSource;
  mine?: boolean;
  dueOnly?: boolean;
  includeClosed?: boolean;
  search?: string;
  page?: number;
  pageSize?: number;
}

// ------------------------------------------------------------------ follow-ups

export interface FollowUpListItem {
  id: string;
  patientId: string;
  uhid: string;
  patientName: string;
  phoneMasked: string | null;
  type: FollowUpType;
  dueDate: string;
  status: FollowUpStatus;
  priority: FollowUpPriority;
  assigneeUserId: string | null;
  attempts: number;
  outcome: RecordedFollowUpOutcome | null;
  isOverdue: boolean;
  /** When the "follow-up due" SMS was queued, by staff or the reminder job. */
  reminderSentAt: string | null;
}

export interface FollowUpDetail {
  id: string;
  patientId: string;
  uhid: string;
  patientName: string;
  phone: string | null;
  visitId: string | null;
  admissionId: string | null;
  type: FollowUpType;
  dueDate: string;
  status: FollowUpStatus;
  priority: FollowUpPriority;
  assigneeUserId: string | null;
  notes: string | null;
  attempts: number;
  outcome: RecordedFollowUpOutcome | null;
  outcomeNotes: string | null;
  completedAt: string | null;
  source: FollowUpSource;
  isOverdue: boolean;
  reminderSentAt: string | null;
  version: number;
}

export interface CreateFollowUpRequest {
  patientId: string;
  visitId: string | null;
  type: FollowUpType;
  dueDate: string;
  assigneeUserId: string | null;
  priority: FollowUpPriority;
  notes: string | null;
}

export interface FollowUpFilters {
  status?: FollowUpStatus;
  type?: FollowUpType;
  priority?: FollowUpPriority;
  mine?: boolean;
  overdueOnly?: boolean;
  includeClosed?: boolean;
  dueFrom?: string;
  dueTo?: string;
  search?: string;
  page?: number;
}

// ------------------------------------------------------------------ referring doctors

export interface ReferringDoctorListItem {
  id: string;
  name: string;
  clinic: string | null;
  specialty: string | null;
  city: string | null;
  phone: string | null;
  isActive: boolean;
  referrals: number;
  lastReferralAt: string | null;
}

export interface ReferringDoctorDetail {
  id: string;
  name: string;
  clinic: string | null;
  phone: string | null;
  email: string | null;
  specialty: string | null;
  city: string | null;
  registrationNumber: string | null;
  alternateNames: string[];
  notes: string | null;
  isActive: boolean;
  version: number;
}

export interface SaveReferringDoctorRequest {
  name: string;
  clinic: string | null;
  phone: string | null;
  email: string | null;
  specialty: string | null;
  city: string | null;
  registrationNumber: string | null;
  alternateNames: string[] | null;
  notes: string | null;
  isActive: boolean;
  version: number | null;
}

export interface ReferralStats {
  doctorId: string;
  from: string;
  to: string;
  referrals: number;
  lastReferralAt: string | null;
  byMonth: CountByLabel[];
  byDepartment: CountByLabel[];
  byVisitType: CountByLabel[];
}

export interface ReferrerText {
  text: string;
  visits: number;
  lastVisitAt: string;
}

// ------------------------------------------------------------------ campaigns

export interface SegmentInput {
  minAge: number | null;
  maxAge: number | null;
  gender: Gender | null;
  city: string | null;
  lastVisitFrom: string | null;
  lastVisitTo: string | null;
  departmentIds: string[] | null;
}

export interface AudiencePreview {
  count: number;
  sample: { uhid: string; maskedName: string; age: string | null; city: string | null }[];
}

export interface SaveCampaignRequest {
  name: string;
  channel: CampaignChannel;
  templateKey: string;
  message: string;
  segment: SegmentInput;
  version: number | null;
}

export interface CampaignListItem {
  id: string;
  name: string;
  channel: CampaignChannel;
  status: CampaignStatus;
  scheduledAt: string | null;
  audienceCount: number;
  queuedCount: number;
  skippedCount: number;
  createdAt: string;
}

export interface CampaignDetail {
  id: string;
  name: string;
  channel: CampaignChannel;
  templateKey: string;
  message: string;
  status: CampaignStatus;
  segment: SegmentInput;
  scheduledAt: string | null;
  startedAt: string | null;
  completedAt: string | null;
  cancelReason: string | null;
  audienceCount: number;
  queuedCount: number;
  skippedCount: number;
  version: number;
}

export interface CampaignDelivery {
  recipients: number;
  queued: number;
  sent: number;
  failed: number;
  skipped: number;
  optedOut: number;
}

// ------------------------------------------------------------------ patient 360

export type Sentiment = 'Negative' | 'Neutral' | 'Positive';
export type ComplaintStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed';
export type ComplaintPriority = 'Normal' | 'High';

export interface Patient360View {
  patient: {
    id: string;
    uhid: string;
    fullName: string;
    gender: Gender;
    age: string | null;
    phoneMasked: string | null;
    city: string | null;
    category: PatientCategory;
    preferredLanguage: string | null;
    isVip: boolean;
    isDeceased: boolean;
    registeredAt: string;
  };
  visits: {
    total: number;
    lastTwelveMonths: number;
    firstVisitAt: string | null;
    lastVisitAt: string | null;
    lastReferredBy: string | null;
    byType: CountByLabel[];
    topDepartments: CountByLabel[];
  };
  spend: {
    totalBilled: number;
    totalPaid: number;
    billedLastTwelveMonths: number;
    bills: number;
    lastBilledAt: string | null;
  };
  feedback: {
    responses: number;
    averageRating: number | null;
    lastNps: number | null;
    lastSentiment: Sentiment | null;
    lowScores: number;
    lastSubmittedAt: string | null;
  };
  recentComplaints: {
    id: string;
    complaintNumber: string;
    status: ComplaintStatus;
    priority: ComplaintPriority;
    category: string | null;
    raisedAt: string;
  }[];
  openFollowUps: {
    id: string;
    type: FollowUpType;
    dueDate: string;
    status: FollowUpStatus;
    priority: FollowUpPriority;
    isOverdue: boolean;
  }[];
  communication: {
    marketingConsent: boolean;
    updatesConsent: boolean;
    marketingChangedAt: string | null;
    campaignsReceived: number;
    lastCampaignAt: string | null;
    optedOutByLink: boolean;
  };
  sourceLead: {
    id: string;
    leadNumber: string;
    source: LeadSource;
    sourceDetail: string | null;
    createdAt: string;
    convertedAt: string | null;
  } | null;
}

// ------------------------------------------------------------------ queries

export const crmKeys = {
  all: ['crm'] as const,
  leads: ['crm', 'leads'] as const,
  followUps: ['crm', 'follow-ups'] as const,
  referrals: ['crm', 'referrals'] as const,
  campaigns: ['crm', 'campaigns'] as const,
  patient360: ['crm', 'patient360'] as const,
};

export const crmQueries = {
  leads: (filters: LeadFilters) =>
    queryOptions({
      queryKey: ['crm', 'leads', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<LeadListItem>>('/api/v1/crm/leads', {
          query: { ...filters, pageSize: filters.pageSize ?? 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  lead: (id: string) =>
    queryOptions({
      queryKey: ['crm', 'leads', 'detail', id],
      queryFn: ({ signal }) => api.get<LeadDetail>(`/api/v1/crm/leads/${id}`, { signal }),
    }),
  followUps: (filters: FollowUpFilters) =>
    queryOptions({
      queryKey: ['crm', 'follow-ups', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<FollowUpListItem>>('/api/v1/crm/follow-ups', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 120_000,
    }),
  followUp: (id: string) =>
    queryOptions({
      queryKey: ['crm', 'follow-ups', 'detail', id],
      queryFn: ({ signal }) => api.get<FollowUpDetail>(`/api/v1/crm/follow-ups/${id}`, { signal }),
      // The full number is audited on every read; do not refetch it behind the executive's back.
      staleTime: 5 * 60_000,
      refetchOnWindowFocus: false,
    }),
  referringDoctors: (filters: { search?: string; activeOnly?: boolean; from?: string; to?: string; page?: number }) =>
    queryOptions({
      queryKey: ['crm', 'referrals', 'doctors', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ReferringDoctorListItem>>('/api/v1/crm/referring-doctors', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  referringDoctor: (id: string) =>
    queryOptions({
      queryKey: ['crm', 'referrals', 'doctor', id],
      queryFn: ({ signal }) => api.get<ReferringDoctorDetail>(`/api/v1/crm/referring-doctors/${id}`, { signal }),
    }),
  referralStats: (id: string, from?: string, to?: string) =>
    queryOptions({
      queryKey: ['crm', 'referrals', 'stats', id, from ?? '', to ?? ''],
      queryFn: ({ signal }) =>
        api.get<ReferralStats>(`/api/v1/crm/referring-doctors/${id}/stats`, { query: { from, to }, signal }),
    }),
  unmatchedReferrals: (from?: string, to?: string) =>
    queryOptions({
      queryKey: ['crm', 'referrals', 'unmatched', from ?? '', to ?? ''],
      queryFn: ({ signal }) =>
        api.get<ReferrerText[]>('/api/v1/crm/referrals/unmatched', { query: { from, to }, signal }),
    }),
  campaigns: (filters: { status?: CampaignStatus; channel?: CampaignChannel; search?: string; page?: number }) =>
    queryOptions({
      queryKey: ['crm', 'campaigns', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<CampaignListItem>>('/api/v1/crm/campaigns', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  campaign: (id: string) =>
    queryOptions({
      queryKey: ['crm', 'campaigns', 'detail', id],
      queryFn: ({ signal }) => api.get<CampaignDetail>(`/api/v1/crm/campaigns/${id}`, { signal }),
    }),
  delivery: (id: string, live: boolean) =>
    queryOptions({
      queryKey: ['crm', 'campaigns', 'delivery', id],
      queryFn: ({ signal }) => api.get<CampaignDelivery>(`/api/v1/crm/campaigns/${id}/delivery`, { signal }),
      refetchInterval: live ? 30_000 : false,
    }),
  patient360: (patientId: string) =>
    queryOptions({
      queryKey: ['crm', 'patient360', patientId],
      queryFn: ({ signal }) => api.get<Patient360View>(`/api/v1/crm/patients/${patientId}/360`, { signal }),
      // Each view is audited as a patient-record access; avoid silent refetches.
      staleTime: 5 * 60_000,
      refetchOnWindowFocus: false,
      retry: false,
    }),
};

// ------------------------------------------------------------------ commands

export const crmApi = {
  createLead: (request: SaveLeadRequest) => api.post<LeadCreated>('/api/v1/crm/leads', request),
  updateLead: (id: string, request: SaveLeadRequest) => api.put<void>(`/api/v1/crm/leads/${id}`, request),
  addActivity: (id: string, request: AddLeadActivityRequest) =>
    api.post<string>(`/api/v1/crm/leads/${id}/activities`, request),
  changeLeadStatus: (id: string, status: LeadStatus, lostReason: string | null) =>
    api.post<void>(`/api/v1/crm/leads/${id}/status`, { status, lostReason }),
  convertLead: (id: string, request: ConvertLeadRequest) =>
    api.post<LeadConversion>(`/api/v1/crm/leads/${id}/convert`, request),

  createFollowUp: (request: CreateFollowUpRequest) => api.post<string>('/api/v1/crm/follow-ups', request),
  updateFollowUp: (
    id: string,
    request: {
      dueDate: string;
      assigneeUserId: string | null;
      priority: FollowUpPriority;
      notes: string | null;
      version: number;
    },
  ) => api.put<void>(`/api/v1/crm/follow-ups/${id}`, request),
  recordAttempt: (
    id: string,
    request: { outcome: FollowUpOutcome; notes: string | null; nextDueDate: string | null },
  ) => api.post<void>(`/api/v1/crm/follow-ups/${id}/attempts`, request),
  cancelFollowUp: (id: string, reason: string) => api.post<void>(`/api/v1/crm/follow-ups/${id}/cancel`, { reason }),
  remindFollowUp: (id: string) => api.post<void>(`/api/v1/crm/follow-ups/${id}/remind`),

  saveReferringDoctor: (id: string | null, request: SaveReferringDoctorRequest) =>
    id
      ? api.put<void>(`/api/v1/crm/referring-doctors/${id}`, request)
      : api.post<string>('/api/v1/crm/referring-doctors', request),

  previewAudience: (segment: SegmentInput) =>
    api.post<AudiencePreview>('/api/v1/crm/campaigns/audience-preview', segment),
  createCampaign: (request: SaveCampaignRequest) => api.post<string>('/api/v1/crm/campaigns', request),
  updateCampaign: (id: string, request: SaveCampaignRequest) => api.put<void>(`/api/v1/crm/campaigns/${id}`, request),
  scheduleCampaign: (id: string, scheduledAt: string) =>
    api.post<void>(`/api/v1/crm/campaigns/${id}/schedule`, { scheduledAt }),
  unscheduleCampaign: (id: string) => api.post<void>(`/api/v1/crm/campaigns/${id}/unschedule`),
  cancelCampaign: (id: string, reason: string) => api.post<void>(`/api/v1/crm/campaigns/${id}/cancel`, { reason }),

  marketingOptOut: (patientId: string, reason: string) =>
    api.post<void>(`/api/v1/crm/patients/${patientId}/marketing-opt-out`, { reason }),
};
