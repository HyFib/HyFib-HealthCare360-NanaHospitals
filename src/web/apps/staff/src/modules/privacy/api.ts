import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  BreachDataCategory,
  BreachNature,
  BreachStatus,
  CorrectableField,
  CorrectionDecision,
  DataRequestChannel,
  DataRequestEventKind,
  DataRequestStatus,
  DataRequestType,
  ErasureItem,
  IdentityCheck,
  RequesterRelation,
  RequestOutcome,
  RetentionAction,
  RetentionRunTrigger,
  RetentionUnit,
  StaffChannel,
  StaffIdentityCheck,
} from './labels';

/** DTOs mirror the Privacy module's C# records (camelCase; enums as strings; DateOnly as "yyyy-MM-dd"). */

// ------------------------------------------------------------------ overview

export interface PrivacyOverview {
  open: number;
  overdue: number;
  dueThisWeek: number;
  awaitingIdentityCheck: number;
  openBreaches: number;
  breachReportsOverdue: number;
}

// ------------------------------------------------------------------ requests

export interface DataRequestListItem {
  id: string;
  requestNumber: string;
  type: DataRequestType;
  status: DataRequestStatus;
  channel: DataRequestChannel;
  patientId: string | null;
  uhid: string | null;
  principalName: string;
  requestedBy: RequesterRelation;
  receivedAt: string;
  dueAt: string;
  isOverdue: boolean;
  identityVerified: boolean;
  assignedToUserId: string | null;
  assignedToName: string | null;
  /** When it was answered or withdrawn; null while open. */
  closedAt: string | null;
}

/** A member of staff a request can be given to: holds privacy.requests.manage in the facility. */
export interface PrivacyOfficer {
  id: string;
  displayName: string;
}

export interface CorrectionItemDto {
  /** Position in the request; decisions refer to it. */
  index: number;
  field: CorrectableField | string;
  fieldLabel: string;
  /** The value on record when the request was made (phone numbers masked). */
  currentValue: string | null;
  requestedValue: string;
  decision: CorrectionDecision;
  note: string | null;
}

export interface DataRequestEventDto {
  at: string;
  kind: DataRequestEventKind;
  text: string;
  by: string | null;
  /** Shown to the requester in the portal; staff notes and assignments are not. */
  isPublic: boolean;
}

export interface DataRequestDetail {
  id: string;
  requestNumber: string;
  type: DataRequestType;
  status: DataRequestStatus;
  channel: DataRequestChannel;
  patientId: string | null;
  uhid: string | null;
  principalName: string;
  contactPhone: string | null;
  contactEmail: string | null;
  requestedBy: RequesterRelation;
  requesterName: string | null;
  details: string;
  corrections: CorrectionItemDto[];
  receivedAt: string;
  dueAt: string;
  isOverdue: boolean;
  identityCheck: IdentityCheck | null;
  identityNote: string | null;
  identityVerifiedAt: string | null;
  identityVerifiedBy: string | null;
  assignedToUserId: string | null;
  assignedToName: string | null;
  response: string | null;
  closedAt: string | null;
  closedBy: string | null;
  erasedItems: string[];
  retainedUntil: string | null;
  retainedPermanently: boolean;
  hasSummary: boolean;
  events: DataRequestEventDto[];
  version: number;
}

export interface RequestFilters {
  view?: 'open' | 'overdue' | 'mine' | 'all';
  type?: DataRequestType;
  status?: DataRequestStatus;
  /** Only this patient's requests. */
  patientId?: string;
  search?: string;
  page?: number;
}

export interface CreateDataRequestRequest {
  type: DataRequestType;
  channel: StaffChannel;
  patientId: string | null;
  principalName: string | null;
  contactPhone: string | null;
  contactEmail: string | null;
  requestedBy: RequesterRelation;
  requesterName: string | null;
  details: string | null;
  corrections: { field: CorrectableField; requestedValue: string }[] | null;
  identityCheck: StaffIdentityCheck | null;
  identityNote: string | null;
}

export interface CreatedDataRequest {
  id: string;
  requestNumber: string;
  dueAt: string;
}

// ------------------------------------------------------------------ data summary

export interface PrivacyContact {
  title: string;
  name: string | null;
  email: string | null;
  phone: string | null;
  address: string | null;
}

export interface SummaryPerson {
  uhid: string;
  fullName: string;
  gender: Gender;
  dateOfBirth: string | null;
  phone: string | null;
  alternatePhone: string | null;
  email: string | null;
  address: string | null;
  emergencyContact: string | null;
  abhaNumber: string | null;
  abhaAddress: string | null;
  aadhaarLast4: string | null;
  hasPhoto: boolean;
  registeredAt: string;
  registeredAtFacility: string | null;
}

export interface SummaryItem {
  /** The table it counts ("clinical.encounters"), stable across languages. */
  key: string;
  label: string;
  count: number;
  first: string | null;
  last: string | null;
}

export interface SummaryCategory {
  key: string;
  title: string;
  description: string;
  purpose: string;
  basis: string;
  retention: string;
  /** Which retention period applies, for saying it in another language (the portal does). */
  retentionKind: string;
  items: SummaryItem[];
}

export interface SummaryConsent {
  type: string;
  granted: boolean;
  capturedAt: string;
  revokedAt: string | null;
}

export interface SummaryRecipient {
  name: string;
  role: string;
  data: string;
}

export interface DataSummary {
  generatedAt: string;
  hospital: string;
  person: SummaryPerson;
  categories: SummaryCategory[];
  consents: SummaryConsent[];
  sharedWith: SummaryRecipient[];
  processors: SummaryRecipient[];
  nominee: { name: string; relationship: string } | null;
  contact: PrivacyContact;
  periods: {
    outpatientYears: number;
    inpatientYears: number;
    financeYears: number;
    messageLogDays: number;
    auditYears: number;
  };
}

// ------------------------------------------------------------------ erasure

export interface ErasureOption {
  item: ErasureItem;
  title: string;
  description: string;
  available: boolean;
  reason: string | null;
}

export interface ErasureHoldItem {
  key: string;
  title: string;
  reason: string;
  until: string | null;
  permanently: boolean;
}

export interface ErasureAssessment {
  options: ErasureOption[];
  holds: ErasureHoldItem[];
  keepUntil: string | null;
  keepPermanently: boolean;
}

export interface ErasureOutcome {
  erased: ErasureItem[];
  keepUntil: string | null;
  keepPermanently: boolean;
  /** The line recorded on the request and shown to the patient. */
  summary: string;
}

// ------------------------------------------------------------------ nominee

export interface SaveNomineeRequest {
  name: string;
  relationship: string;
  phone: string | null;
  email: string | null;
  /** The nominee as shown; none when the patient had none. One named meanwhile is not replaced unseen (REL-04). */
  version?: number;
}

export interface NomineeDto {
  name: string;
  relationship: string;
  phone: string | null;
  email: string | null;
  recordedVia: DataRequestChannel;
  updatedAt: string;
  version: number;
}

// ------------------------------------------------------------------ breaches

export interface BreachListItem {
  id: string;
  breachNumber: string;
  title: string;
  status: BreachStatus;
  awareAt: string;
  reportDueAt: string;
  reportOverdue: boolean;
  estimatedAffected: number | null;
  affectedListed: number;
  notified: number;
  boardInformedAt: string | null;
  reportSubmittedAt: string | null;
}

export interface BreachCreated {
  id: string;
  breachNumber: string;
}

export interface BreachDetail {
  id: string;
  breachNumber: string;
  title: string;
  description: string;
  status: BreachStatus;
  awareAt: string;
  occurredFrom: string | null;
  occurredTo: string | null;
  nature: BreachNature[];
  location: string | null;
  dataCategories: BreachDataCategory[];
  estimatedAffected: number | null;
  consequences: string | null;
  mitigation: string | null;
  principalSteps: string | null;
  /** Rule 7(2)(b)(i): the events, circumstances and reasons that led to the breach. */
  cause: string | null;
  /** Rule 7(2)(b)(iii): any findings about the person who caused it. Optional. */
  findings: string | null;
  remediation: string | null;
  principalNotice: string | null;
  boardInformedAt: string | null;
  reportDueAt: string;
  extendedUntil: string | null;
  reportSubmittedAt: string | null;
  boardReference: string | null;
  closedAt: string | null;
  closingNote: string | null;
  notifiedAt: string | null;
  reportOverdue: boolean;
  affectedListed: number;
  notified: number;
  withoutContact: number;
  /** What the detailed report must cover that is still empty, in words ("the cause"). */
  missingForReport: string[];
  /** What the notice to people must carry that is still empty. */
  missingForNotice: string[];
  version: number;
}

export interface SaveBreachRequest {
  title: string;
  description: string;
  awareAt: string;
  occurredFrom: string | null;
  occurredTo: string | null;
  nature: BreachNature[];
  location: string | null;
  dataCategories: BreachDataCategory[];
  estimatedAffected: number | null;
  consequences: string | null;
  mitigation: string | null;
  principalSteps: string | null;
  /** Rule 7(2)(b)(i): the events, circumstances and reasons that led to the breach. */
  cause: string | null;
  /** Rule 7(2)(b)(iii): any findings about the person who caused it. Optional. */
  findings: string | null;
  remediation: string | null;
  principalNotice: string | null;
  version: number | null;
}

export interface BreachReport {
  hospital: string;
  legalName: string | null;
  address: string | null;
  registrationNumber: string | null;
  contact: PrivacyContact;
  breach: BreachDetail;
  generatedAt: string;
}

export interface AffectedPatientItem {
  patientId: string;
  uhid: string;
  fullName: string;
  /** Masked by the server ("+91******3210"). */
  phone: string | null;
  hasEmail: boolean;
  addedAt: string;
  notifiedAt: string | null;
  /** "sms,email", or "none" when the patient had no contact details. */
  noticeChannels: string | null;
}

export interface AddAffectedResult {
  added: number;
  alreadyListed: number;
  notFound: string[];
}

export interface NotifyAffectedResult {
  queued: number;
  withoutContact: number;
  alreadyNotified: number;
}

export interface BreachFilters {
  view?: 'open' | 'all';
  search?: string;
  page?: number;
}

// ------------------------------------------------------------------ retention

export interface RetentionRuleDto {
  key: string;
  title: string;
  description: string;
  basis: string;
  action: RetentionAction;
  unit: RetentionUnit;
  /** The period in force; null when kept permanently. */
  value: number | null;
  defaultValue: number | null;
  minimumValue: number | null;
  maximumValue: number;
  editable: boolean;
  lastRemoved: number | null;
  lastRunAt: string | null;
  /** The saved period's version; null while the default applies. */
  version: number | null;
}

export interface RetentionRunResult {
  key: string;
  removed: number;
  before: string;
}

export interface RetentionRunDto {
  id: string;
  startedAt: string;
  finishedAt: string | null;
  trigger: RetentionRunTrigger;
  startedByUserId: string | null;
  startedByName: string | null;
  totalRemoved: number;
  results: RetentionRunResult[];
  error: string | null;
}

export interface ErasureHold {
  requestId: string;
  requestNumber: string;
  patientId: string | null;
  uhid: string | null;
  principalName: string;
  retainedUntil: string | null;
  retainedPermanently: boolean;
  isDue: boolean;
}

export interface RetentionReview {
  outpatientChartsDue: number;
  inpatientChartsDue: number;
  erasureHolds: ErasureHold[];
}

// ------------------------------------------------------------------ settings

export interface PrivacySettingsView {
  /** Each with its version (null until first saved), sent back with a change (REL-04). */
  settings: {
    contact: PrivacyContact;
    responseDays: number;
    version: number | null;
    responseDaysVersion: number | null;
  };
  /** What patients see: the saved contact with the hospital's own details filling any gap. */
  published: PrivacyContact;
}

export interface SavePrivacySettingsRequest {
  title: string | null;
  name: string | null;
  email: string | null;
  phone: string | null;
  address: string | null;
  responseDays: number;
  /** As loaded: settings changed meanwhile are not overwritten (REL-04). */
  version: number | null;
  responseDaysVersion: number | null;
}

// ------------------------------------------------------------------ queries

const BASE = '/api/v1/privacy';
export const REQUEST_PAGE_SIZE = 25;
export const BREACH_PAGE_SIZE = 25;
export const AFFECTED_PAGE_SIZE = 25;

export const privacyKeys = {
  all: ['privacy'] as const,
  overview: ['privacy', 'overview'] as const,
  /** Lists and details of requests (invalidate after any change to a request). */
  requests: ['privacy', 'requests'] as const,
  request: (id: string) => ['privacy', 'requests', 'detail', id] as const,
  /** Opening these is audited as record access, so they are kept apart from the request lists. */
  summary: (id: string) => ['privacy', 'summary', id] as const,
  erasure: (id: string) => ['privacy', 'erasure', id] as const,
  nominee: (patientId: string) => ['privacy', 'nominee', patientId] as const,
  officers: ['privacy', 'officers'] as const,
  breaches: ['privacy', 'breaches'] as const,
  breach: (id: string) => ['privacy', 'breaches', 'detail', id] as const,
  affected: (id: string) => ['privacy', 'breaches', 'affected', id] as const,
  retention: ['privacy', 'retention'] as const,
  settings: ['privacy', 'settings'] as const,
};

export const privacyQueries = {
  overview: () =>
    queryOptions({
      queryKey: privacyKeys.overview,
      queryFn: ({ signal }) => api.get<PrivacyOverview>(`${BASE}/overview`, { signal }),
      staleTime: 60_000,
      refetchInterval: 120_000,
    }),
  requests: (filters: RequestFilters) =>
    queryOptions({
      queryKey: ['privacy', 'requests', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<DataRequestListItem>>(`${BASE}/requests`, {
          query: {
            search: filters.search,
            type: filters.type,
            patientId: filters.patientId,
            status: filters.view === 'all' ? filters.status : undefined,
            openOnly: filters.view === 'open' || filters.view === 'mine' ? true : undefined,
            overdueOnly: filters.view === 'overdue' ? true : undefined,
            mine: filters.view === 'mine' ? true : undefined,
            page: filters.page,
            pageSize: REQUEST_PAGE_SIZE,
          },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 120_000,
    }),
  request: (id: string) =>
    queryOptions({
      queryKey: privacyKeys.request(id),
      queryFn: ({ signal }) => api.get<DataRequestDetail>(`${BASE}/requests/${id}`, { signal }),
      // Opening a request is audited as patient-record access: never refetch it silently.
      staleTime: 60_000,
      refetchOnWindowFocus: false,
      retry: false,
    }),
  summary: (id: string) =>
    queryOptions({
      queryKey: privacyKeys.summary(id),
      queryFn: ({ signal }) => api.get<DataSummary>(`${BASE}/requests/${id}/summary`, { signal }),
      staleTime: 5 * 60_000,
      refetchOnWindowFocus: false,
      retry: false,
    }),
  erasure: (id: string) =>
    queryOptions({
      queryKey: privacyKeys.erasure(id),
      queryFn: ({ signal }) => api.get<ErasureAssessment>(`${BASE}/requests/${id}/erasure`, { signal }),
      staleTime: 60_000,
      refetchOnWindowFocus: false,
      retry: false,
    }),
  /** How many open requests a patient has (for the patient record's privacy card). */
  openForPatient: (patientId: string) =>
    queryOptions({
      queryKey: ['privacy', 'requests', 'open-for-patient', patientId],
      queryFn: ({ signal }) =>
        api.get<PagedResult<DataRequestListItem>>(`${BASE}/requests`, {
          query: { patientId, openOnly: true, pageSize: 1 },
          signal,
        }),
      staleTime: 60_000,
      retry: false,
    }),
  officers: () =>
    queryOptions({
      queryKey: privacyKeys.officers,
      queryFn: ({ signal }) => api.get<PrivacyOfficer[]>(`${BASE}/officers`, { signal }),
      staleTime: 5 * 60_000,
    }),
  nominee: (patientId: string) =>
    queryOptions({
      queryKey: privacyKeys.nominee(patientId),
      queryFn: ({ signal }) =>
        api.get<{ nominee: NomineeDto | null }>(`${BASE}/patients/${patientId}/nominee`, { signal }),
      staleTime: 5 * 60_000,
      refetchOnWindowFocus: false,
      retry: false,
    }),
  breaches: (filters: BreachFilters) =>
    queryOptions({
      queryKey: ['privacy', 'breaches', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<BreachListItem>>(`${BASE}/breaches`, {
          query: {
            openOnly: filters.view === 'all' ? undefined : true,
            search: filters.search,
            page: filters.page,
            pageSize: BREACH_PAGE_SIZE,
          },
          signal,
        }),
      placeholderData: keepPreviousData,
      // The 72-hour clock is worked out against the time of the last refresh.
      refetchInterval: 60_000,
    }),
  breach: (id: string) =>
    queryOptions({
      queryKey: privacyKeys.breach(id),
      queryFn: ({ signal }) => api.get<BreachDetail>(`${BASE}/breaches/${id}`, { signal }),
      refetchInterval: 60_000,
      retry: false,
    }),
  affected: (id: string, page: number) =>
    queryOptions({
      queryKey: [...privacyKeys.affected(id), page],
      queryFn: ({ signal }) =>
        api.get<PagedResult<AffectedPatientItem>>(`${BASE}/breaches/${id}/affected`, {
          query: { page, pageSize: AFFECTED_PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  retentionRules: () =>
    queryOptions({
      queryKey: ['privacy', 'retention', 'rules'],
      queryFn: ({ signal }) => api.get<RetentionRuleDto[]>(`${BASE}/retention`, { signal }),
    }),
  retentionRuns: () =>
    queryOptions({
      queryKey: ['privacy', 'retention', 'runs'],
      queryFn: ({ signal }) => api.get<RetentionRunDto[]>(`${BASE}/retention/runs`, { query: { take: 10 }, signal }),
      // A clean-up asked for now runs within minutes; keep the list fresh while the page is open.
      refetchInterval: 30_000,
    }),
  retentionPreview: () =>
    queryOptions({
      queryKey: ['privacy', 'retention', 'preview'],
      queryFn: ({ signal }) => api.get<RetentionRunResult[]>(`${BASE}/retention/preview`, { signal }),
      staleTime: 0,
      gcTime: 60_000,
    }),
  retentionReview: () =>
    queryOptions({
      queryKey: ['privacy', 'retention', 'review'],
      queryFn: ({ signal }) => api.get<RetentionReview>(`${BASE}/retention/review`, { signal }),
    }),
  settings: () =>
    queryOptions({
      queryKey: privacyKeys.settings,
      queryFn: ({ signal }) => api.get<PrivacySettingsView>(`${BASE}/settings`, { signal }),
    }),
};

// ------------------------------------------------------------------ commands

export const privacyApi = {
  createRequest: (request: CreateDataRequestRequest) => api.post<CreatedDataRequest>(`${BASE}/requests`, request),
  start: (id: string) => api.post<void>(`${BASE}/requests/${id}/start`),
  verifyIdentity: (id: string, check: StaffIdentityCheck, note: string | null) =>
    api.post<void>(`${BASE}/requests/${id}/verify`, { check, note }),
  assign: (id: string, userId: string | null) => api.post<void>(`${BASE}/requests/${id}/assign`, { userId }),
  addNote: (id: string, text: string) => api.post<void>(`${BASE}/requests/${id}/notes`, { text }),
  decideCorrections: (id: string, items: { index: number; decision: 'Accepted' | 'Declined'; note: string | null }[]) =>
    api.post<void>(`${BASE}/requests/${id}/corrections`, { items }),
  erase: (id: string, items: ErasureItem[]) => api.post<ErasureOutcome>(`${BASE}/requests/${id}/erasure`, { items }),
  close: (id: string, outcome: RequestOutcome, response: string) =>
    api.post<void>(`${BASE}/requests/${id}/close`, { outcome, response }),

  createBreach: (request: SaveBreachRequest) => api.post<BreachCreated>(`${BASE}/breaches`, request),
  updateBreach: (id: string, request: SaveBreachRequest) => api.put<void>(`${BASE}/breaches/${id}`, request),
  boardInformed: (id: string, at: string, reference: string | null) =>
    api.post<void>(`${BASE}/breaches/${id}/board-informed`, { at, reference }),
  extension: (id: string, until: string, reference: string | null) =>
    api.post<void>(`${BASE}/breaches/${id}/extension`, { until, reference }),
  reportSubmitted: (id: string, at: string, reference: string | null) =>
    api.post<void>(`${BASE}/breaches/${id}/report-submitted`, { at, reference }),
  closeBreach: (id: string, note: string | null) => api.post<void>(`${BASE}/breaches/${id}/close`, { note }),
  /** The detailed report for printing (audited). */
  breachReport: (id: string) => api.get<BreachReport>(`${BASE}/breaches/${id}/report`),
  addAffected: (id: string, patientIds: string[], uhids: string[]) =>
    api.post<AddAffectedResult>(`${BASE}/breaches/${id}/affected`, {
      patientIds: patientIds.length > 0 ? patientIds : null,
      uhids: uhids.length > 0 ? uhids : null,
    }),
  removeAffected: (id: string, patientId: string) => api.delete<void>(`${BASE}/breaches/${id}/affected/${patientId}`),
  notify: (id: string) => api.post<NotifyAffectedResult>(`${BASE}/breaches/${id}/notify`),

  /** `version`: the period as shown (null while the default applies); one changed meanwhile is not overwritten. */
  updateRetention: (key: string, value: number, version: number | null) =>
    api.put<void>(`${BASE}/retention/${encodeURIComponent(key)}`, { value, version }),
  runRetention: () => api.post<void>(`${BASE}/retention/run`),

  saveSettings: (request: SavePrivacySettingsRequest) => api.put<void>(`${BASE}/settings`, request),

  saveNominee: (patientId: string, request: SaveNomineeRequest) =>
    api.put<void>(`${BASE}/patients/${patientId}/nominee`, request),
  removeNominee: (patientId: string) => api.delete<void>(`${BASE}/patients/${patientId}/nominee`),
};
