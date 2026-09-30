import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  DataSummary,
  NomineeView,
  PortalBreachNotice,
  PortalDataRequestCreate,
  PortalDataRequestDetail,
  PortalDataRequestItem,
  PortalNomineeRequest,
  PortalPrivacyStatus,
  PortalRequestCreated,
  PublicPrivacyInfo,
} from '@/lib/types';

const base = '/api/v1/portal/privacy';
const id = (value: string) => encodeURIComponent(value);

/**
 * The privacy centre of the person being viewed. Every key starts `['portal', patientId, 'privacy']`, so one person's
 * data never shows under another's name and a command can refresh everything at once (`privacyQueries.all`).
 * Opening the summary, a request or the nominee is audited by the server as access to the patient's record: pages
 * that only need counts or the nominee's name use `status`, which is not, and the summary is kept for a few minutes
 * rather than fetched on every visit.
 */
export const privacyQueries = {
  all: (patientId: string) => ['portal', patientId, 'privacy'] as const,

  /** Whether a nominee is named (and their name), open requests and the portal's limit, and how many notices. */
  status: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'privacy', 'status'],
      queryFn: ({ signal }) => api.get<PortalPrivacyStatus>(`${base}/status`, { query: { patientId }, signal }),
    }),

  summary: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'privacy', 'summary'],
      queryFn: ({ signal }) => api.get<DataSummary>(`${base}/summary`, { query: { patientId }, signal }),
      staleTime: 5 * 60_000,
    }),

  requests: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'privacy', 'requests'],
      queryFn: ({ signal }) => api.get<PortalDataRequestItem[]>(`${base}/requests`, { query: { patientId }, signal }),
    }),

  request: (patientId: string, requestId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'privacy', 'requests', 'detail', requestId],
      queryFn: ({ signal }) => api.get<PortalDataRequestDetail>(`${base}/requests/${id(requestId)}`, { signal }),
    }),

  nominee: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'privacy', 'nominee'],
      queryFn: ({ signal }) => api.get<NomineeView>(`${base}/nominee`, { query: { patientId }, signal }),
      staleTime: 60_000,
    }),

  notices: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'privacy', 'notices'],
      queryFn: ({ signal }) => api.get<PortalBreachNotice[]>(`${base}/notices`, { query: { patientId }, signal }),
      staleTime: 5 * 60_000,
    }),

  notice: (patientId: string, noticeId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'privacy', 'notices', noticeId],
      queryFn: ({ signal }) => api.get<PortalBreachNotice>(`${base}/notices/${id(noticeId)}`, { signal }),
      staleTime: 5 * 60_000,
    }),
};

export const privacyApi = {
  createRequest: (request: PortalDataRequestCreate) => api.post<PortalRequestCreated>(`${base}/requests`, request),
  withdraw: (requestId: string) => api.post<void>(`${base}/requests/${id(requestId)}/withdraw`),
  saveNominee: (request: PortalNomineeRequest) => api.put<void>(`${base}/nominee`, request),
  removeNominee: (patientId: string) => api.delete<void>(`${base}/nominee`, { query: { patientId } }),
};

/** The grievance officer and the published reply period (anonymous; the privacy notice reads it without sign-in). */
export const publicPrivacyQueries = {
  info: () =>
    queryOptions({
      queryKey: ['public', 'privacy'],
      queryFn: ({ signal }) => api.get<PublicPrivacyInfo>('/api/v1/public/privacy', { signal }),
      staleTime: 30 * 60_000,
    }),
};
