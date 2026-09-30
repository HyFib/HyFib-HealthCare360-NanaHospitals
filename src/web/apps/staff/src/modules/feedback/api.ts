import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  ComplaintPriority,
  ComplaintSource,
  ComplaintStatus,
  FeedbackAspect,
  FeedbackChannel,
  FeedbackFormType,
  Sentiment,
} from './labels';

/** A labelled count for breakdown charts (the server's CountByLabel). */
export interface CountByLabel {
  label: string;
  count: number;
}

// ------------------------------------------------------------------ forms

export interface FeedbackFormDto {
  id: string;
  code: string;
  title: string;
  type: FeedbackFormType;
  aspects: FeedbackAspect[];
  includesNps: boolean;
  allowsComment: boolean;
  introText: string | null;
  thankYouText: string | null;
  departmentId: string | null;
  publicToken: string;
  /** Null until the `portal.baseUrl` setting holds the hospital's https portal address. */
  qrUrl: string | null;
  isActive: boolean;
  version: number;
}

export interface SaveFeedbackFormRequest {
  code: string;
  title: string;
  type: FeedbackFormType;
  aspects: FeedbackAspect[];
  includesNps: boolean;
  allowsComment: boolean;
  introText: string | null;
  thankYouText: string | null;
  departmentId: string | null;
  isActive: boolean;
  version: number | null;
}

// ------------------------------------------------------------------ responses

/** 1–5 per aspect (camelCase keys of the server's RatingsInput); left out when not asked or not answered. */
export interface RatingsInput {
  doctor?: number | null;
  nursing?: number | null;
  frontDesk?: number | null;
  billing?: number | null;
  cleanliness?: number | null;
  food?: number | null;
  overall?: number | null;
}

export interface FeedbackResponseListItem {
  id: string;
  submittedAt: string;
  formId: string;
  formTitle: string;
  channel: FeedbackChannel;
  departmentId: string | null;
  averageRating: number | null;
  nps: number | null;
  sentiment: Sentiment;
  isLowScore: boolean;
  hasComment: boolean;
  isAnonymous: boolean;
  complaintNumber: string | null;
}

export interface FeedbackResponseDetail {
  id: string;
  formId: string;
  formTitle: string;
  channel: FeedbackChannel;
  submittedAt: string;
  patientId: string | null;
  uhid: string | null;
  patientName: string | null;
  visitId: string | null;
  admissionId: string | null;
  departmentId: string | null;
  ratings: RatingsInput;
  nps: number | null;
  comment: string | null;
  sentiment: Sentiment;
  sentimentScore: number;
  isLowScore: boolean;
  wantsCallback: boolean;
  contactPhone: string | null;
  complaintId: string | null;
  complaintNumber: string | null;
}

export interface ResponseFilters {
  formId?: string;
  departmentId?: string;
  patientId?: string;
  channel?: FeedbackChannel;
  sentiment?: Sentiment;
  lowOnly?: boolean;
  from?: string;
  to?: string;
  page?: number;
}

export interface StaffFeedbackRequest {
  formId: string;
  patientId: string;
  visitId: string | null;
  ratings: RatingsInput | null;
  nps: number | null;
  comment: string | null;
  wantsCallback: boolean;
  contactPhone: string | null;
}

export interface FeedbackOutcome {
  responseId: string;
  isLowScore: boolean;
  sentiment: Sentiment;
  complaintNumber: string | null;
  thankYou: string;
}

// ------------------------------------------------------------------ analytics

export interface NpsResult {
  responses: number;
  promoters: number;
  passives: number;
  detractors: number;
  score: number | null;
}

export interface FeedbackAnalytics {
  from: string;
  to: string;
  responses: number;
  nps: NpsResult;
  averageRating: number | null;
  lowScores: number;
  aspects: { aspect: FeedbackAspect; responses: number; average: number | null }[];
  bySentiment: CountByLabel[];
  byChannel: CountByLabel[];
  /** One row per calendar month in the period ("yyyy-MM"), oldest first. */
  trend: { month: string; responses: number; nps: number | null; averageRating: number | null; lowScores: number }[];
  departments: {
    departmentId: string | null;
    department: string;
    responses: number;
    averageRating: number | null;
    nps: number | null;
  }[];
  complaints: {
    raised: number;
    open: number;
    overdue: number;
    resolved: number;
    averageTurnaroundHours: number | null;
    resolvedWithinSlaPercent: number | null;
  };
}

export interface AnalyticsFilters {
  from?: string;
  to?: string;
  formId?: string;
  departmentId?: string;
}

// ------------------------------------------------------------------ complaints

export interface ComplaintListItem {
  id: string;
  complaintNumber: string;
  source: ComplaintSource;
  status: ComplaintStatus;
  priority: ComplaintPriority;
  category: string | null;
  departmentId: string | null;
  patientId: string | null;
  ownerUserId: string | null;
  raisedAt: string;
  dueAt: string;
  isOverdue: boolean;
  turnaroundHours: number | null;
}

export interface ComplaintDetail {
  id: string;
  complaintNumber: string;
  source: ComplaintSource;
  status: ComplaintStatus;
  priority: ComplaintPriority;
  category: string | null;
  description: string;
  departmentId: string | null;
  patientId: string | null;
  feedbackResponseId: string | null;
  contactName: string | null;
  contactPhone: string | null;
  ownerUserId: string | null;
  resolution: string | null;
  raisedAt: string;
  dueAt: string;
  resolvedAt: string | null;
  closedAt: string | null;
  reopenCount: number;
  isOverdue: boolean;
  version: number;
}

export interface ComplaintFilters {
  status?: ComplaintStatus;
  priority?: ComplaintPriority;
  source?: ComplaintSource;
  departmentId?: string;
  mine?: boolean;
  openOnly?: boolean;
  overdueOnly?: boolean;
  from?: string;
  to?: string;
  search?: string;
  page?: number;
}

export interface CreateComplaintRequest {
  source: ComplaintSource;
  patientId: string | null;
  departmentId: string | null;
  category: string | null;
  description: string;
  contactName: string | null;
  contactPhone: string | null;
  priority: ComplaintPriority;
  ownerUserId: string | null;
}

export interface UpdateComplaintRequest {
  ownerUserId: string | null;
  priority: ComplaintPriority;
  departmentId: string | null;
  category: string | null;
  version: number;
}

// ------------------------------------------------------------------ queries

export const feedbackKeys = {
  all: ['feedback'] as const,
  forms: ['feedback', 'forms'] as const,
  responses: ['feedback', 'responses'] as const,
  analytics: ['feedback', 'analytics'] as const,
  complaints: ['feedback', 'complaints'] as const,
};

export const feedbackQueries = {
  forms: (filters: { activeOnly?: boolean; type?: FeedbackFormType } = {}) =>
    queryOptions({
      queryKey: ['feedback', 'forms', 'list', filters],
      queryFn: ({ signal }) => api.get<FeedbackFormDto[]>('/api/v1/feedback/forms', { query: { ...filters }, signal }),
      staleTime: 60_000,
    }),
  form: (id: string) =>
    queryOptions({
      queryKey: ['feedback', 'forms', 'detail', id],
      queryFn: ({ signal }) => api.get<FeedbackFormDto>(`/api/v1/feedback/forms/${id}`, { signal }),
    }),
  responses: (filters: ResponseFilters) =>
    queryOptions({
      queryKey: ['feedback', 'responses', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<FeedbackResponseListItem>>('/api/v1/feedback/responses', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  response: (id: string) =>
    queryOptions({
      queryKey: ['feedback', 'responses', 'detail', id],
      queryFn: ({ signal }) => api.get<FeedbackResponseDetail>(`/api/v1/feedback/responses/${id}`, { signal }),
      // Opening a patient's response is audited as record access; do not refetch it silently.
      staleTime: 5 * 60_000,
      refetchOnWindowFocus: false,
    }),
  analytics: (filters: AnalyticsFilters) =>
    queryOptions({
      queryKey: ['feedback', 'analytics', filters],
      queryFn: ({ signal }) =>
        api.get<FeedbackAnalytics>('/api/v1/feedback/analytics', { query: { ...filters }, signal }),
      placeholderData: keepPreviousData,
      staleTime: 60_000,
    }),
  complaints: (filters: ComplaintFilters) =>
    queryOptions({
      queryKey: ['feedback', 'complaints', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ComplaintListItem>>('/api/v1/feedback/complaints', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 120_000,
    }),
  complaint: (id: string) =>
    queryOptions({
      queryKey: ['feedback', 'complaints', 'detail', id],
      queryFn: ({ signal }) => api.get<ComplaintDetail>(`/api/v1/feedback/complaints/${id}`, { signal }),
      // Audited as patient-record access when the complaint names a patient.
      staleTime: 60_000,
      refetchOnWindowFocus: false,
    }),
};

// ------------------------------------------------------------------ commands

export const feedbackApi = {
  createForm: (request: SaveFeedbackFormRequest) => api.post<string>('/api/v1/feedback/forms', request),
  updateForm: (id: string, request: SaveFeedbackFormRequest) => api.put<void>(`/api/v1/feedback/forms/${id}`, request),
  rotateToken: (id: string) => api.post<FeedbackFormDto>(`/api/v1/feedback/forms/${id}/rotate-token`),

  recordByStaff: (request: StaffFeedbackRequest) => api.post<FeedbackOutcome>('/api/v1/feedback/responses', request),
  sendLink: (patientId: string, visitId: string, formId: string | null) =>
    api.post<{ invitationId: string }>('/api/v1/feedback/invitations', { patientId, visitId, formId }),

  createComplaint: (request: CreateComplaintRequest) => api.post<string>('/api/v1/feedback/complaints', request),
  updateComplaint: (id: string, request: UpdateComplaintRequest) =>
    api.put<void>(`/api/v1/feedback/complaints/${id}`, request),
  changeComplaintStatus: (id: string, status: ComplaintStatus, note: string | null) =>
    api.post<void>(`/api/v1/feedback/complaints/${id}/status`, { status, note }),
};
