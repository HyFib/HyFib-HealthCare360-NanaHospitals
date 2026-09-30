import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { SterilizationMethod } from './rules';

export type CssdPriority = 'Routine' | 'Urgent';
export type CssdRequestStatus = 'Open' | 'PartiallyIssued' | 'Issued' | 'Cancelled';
export type LoadStatus = 'Loaded' | 'Running' | 'Completed' | 'Released' | 'Failed';
export type IndicatorResult = 'Pending' | 'Pass' | 'Fail';
export type PackStatus = 'InProcess' | 'Available' | 'Issued' | 'Returned' | 'Recalled' | 'Reprocess';
export type SetIssueStatus = 'Issued' | 'Used' | 'Returned' | 'Recalled';

export const LOAD_STATUSES: readonly LoadStatus[] = ['Loaded', 'Running', 'Completed', 'Released', 'Failed'];
export const LOAD_STATUS_LABEL: Record<LoadStatus, string> = {
  Loaded: 'Loaded (not started)',
  Running: 'Running',
  Completed: 'Cycle complete',
  Released: 'Released',
  Failed: 'Failed',
};
/** Badge colour of a load status (the badge also shows the status word). */
export function loadStatusTone(status: LoadStatus): 'success' | 'danger' | 'warning' | 'brand' {
  return status === 'Released'
    ? 'success'
    : status === 'Failed'
      ? 'danger'
      : status === 'Completed'
        ? 'warning'
        : 'brand';
}

export const REQUEST_STATUS_LABEL: Record<CssdRequestStatus, string> = {
  Open: 'Open',
  PartiallyIssued: 'Partly issued',
  Issued: 'Issued',
  Cancelled: 'Cancelled',
};
export const PACK_STATUS_LABEL: Record<PackStatus, string> = {
  InProcess: 'In sterilizer',
  Available: 'On shelf',
  Issued: 'Issued',
  Returned: 'Returned soiled',
  Recalled: 'Recalled',
  Reprocess: 'Reprocess',
};
export const ISSUE_STATUS_LABEL: Record<SetIssueStatus, string> = {
  Issued: 'Issued',
  Used: 'Used on patient',
  Returned: 'Returned',
  Recalled: 'Recalled',
};

export interface InstrumentSetItem {
  name: string;
  quantity: number;
}

export interface InstrumentSetDto {
  id: string;
  code: string;
  name: string;
  category: string | null;
  ownerDepartmentId: string | null;
  method: SterilizationMethod;
  shelfLifeDays: number;
  isActive: boolean;
  notes: string | null;
  items: InstrumentSetItem[];
  instrumentCount: number;
  packsAvailable: number;
  version: number;
}

export interface SaveInstrumentSetRequest {
  code: string;
  name: string;
  category: string | null;
  ownerDepartmentId: string | null;
  method: SterilizationMethod;
  shelfLifeDays: number;
  isActive: boolean;
  notes: string | null;
  items: InstrumentSetItem[];
  version: number | null;
}

export interface CssdRequestLineDto {
  instrumentSetId: string;
  setName: string | null;
  quantity: number;
  issuedQuantity: number;
}

export interface CssdRequestDto {
  id: string;
  requestNumber: string;
  departmentId: string;
  departmentName: string | null;
  requestedByName: string | null;
  requestedAt: string;
  requiredBy: string | null;
  priority: CssdPriority;
  status: CssdRequestStatus;
  notes: string | null;
  cancelReason: string | null;
  lines: CssdRequestLineDto[];
}

export interface CreateCssdRequestRequest {
  departmentId: string;
  requiredBy: string | null;
  priority: CssdPriority;
  notes: string | null;
  lines: { instrumentSetId: string; quantity: number }[];
}

export interface SoiledReceiptLineDto {
  instrumentSetId: string;
  setName: string | null;
  setIssueId: string | null;
  expectedCount: number;
  receivedCount: number;
  missingCount: number;
  extraCount: number;
  damagedCount: number;
  discrepancies: string | null;
}

export interface SoiledReceiptDto {
  id: string;
  departmentId: string;
  departmentName: string | null;
  receivedAt: string;
  receivedByName: string | null;
  handedOverByName: string | null;
  hasDiscrepancy: boolean;
  notes: string | null;
  lines: SoiledReceiptLineDto[];
}

export interface ReceiveSoiledRequest {
  departmentId: string;
  handedOverByName: string | null;
  notes: string | null;
  lines: {
    instrumentSetId: string;
    setIssueId: string | null;
    counts: { itemName: string; received: number; damaged: number }[];
  }[];
}

export interface SterilePackDto {
  id: string;
  packNumber: string;
  loadId: string;
  cycleNumber: string | null;
  instrumentSetId: string;
  setName: string | null;
  status: PackStatus;
  sterileUntil: string | null;
  isExpired: boolean;
  issuedToDepartmentId: string | null;
}

export interface SetIssueDto {
  id: string;
  packId: string;
  packNumber: string | null;
  loadId: string;
  cycleNumber: string | null;
  instrumentSetId: string;
  setName: string | null;
  departmentId: string;
  departmentName: string | null;
  issuedAt: string;
  issuedToName: string | null;
  sterileUntil: string | null;
  status: SetIssueStatus;
  patientId: string | null;
  usedAt: string | null;
  recallFlaggedAt: string | null;
  recallReason: string | null;
  recallAcknowledgedAt: string | null;
  recallAction: string | null;
}

export interface IssuePacksRequest {
  departmentId: string;
  cssdRequestId: string | null;
  issuedToName: string | null;
  packIds: string[];
}

export interface CssdSummary {
  from: string;
  to: string;
  loads: number;
  loadsReleased: number;
  loadsFailed: number;
  failureRatePercent: number;
  biologicalPending: number;
  byMethod: { method: SterilizationMethod; loads: number; failed: number }[];
  packsIssued: number;
  openRequests: number;
  receiptsWithDiscrepancy: number;
  openRecalls: number;
  packsExpiringSoon: number;
  availableBySet: { instrumentSetId: string; code: string; name: string; available: number; expiringSoon: number }[];
}

export interface LoadListItem {
  id: string;
  cycleNumber: string;
  sterilizerName: string;
  method: SterilizationMethod;
  status: LoadStatus;
  loadedAt: string;
  completedAt: string | null;
  chemicalIndicator: IndicatorResult;
  biologicalIndicator: IndicatorResult;
  releasedAt: string | null;
  failureReason: string | null;
  containsImplants: boolean;
  packs: number;
}

export interface LoadDto {
  id: string;
  cycleNumber: string;
  sterilizerName: string;
  method: SterilizationMethod;
  program: string | null;
  containsImplants: boolean;
  status: LoadStatus;
  loadedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  temperatureC: number | null;
  pressureKpa: number | null;
  exposureMinutes: number | null;
  chemicalIndicator: IndicatorResult;
  biologicalIndicator: IndicatorResult;
  biologicalIndicatorLot: string | null;
  biologicalReadAt: string | null;
  operatorName: string | null;
  releasedAt: string | null;
  releasedByName: string | null;
  failureReason: string | null;
  notes: string | null;
  packs: SterilePackDto[];
  version: number;
}

export interface CreateLoadRequest {
  sterilizerName: string;
  method: SterilizationMethod;
  program: string | null;
  containsImplants: boolean;
  operatorName: string | null;
  notes: string | null;
  packs: { instrumentSetId: string; quantity: number }[];
}

export interface RecallItem {
  issueId: string;
  packNumber: string | null;
  setName: string | null;
  departmentId: string;
  departmentName: string | null;
  issuedAt: string;
  status: SetIssueStatus;
  patientId: string | null;
  patientUhid: string | null;
  patientName: string | null;
  recallFlaggedAt: string | null;
  recallAcknowledgedAt: string | null;
  recallAction: string | null;
}

export interface RecallReport {
  loadId: string;
  cycleNumber: string;
  recallRequired: boolean;
  packsOnShelfRecalled: number;
  issuesFlagged: number;
  issuesAcknowledged: number;
  patientsExposed: number;
  issues: RecallItem[];
}

export interface LoadFilters {
  status?: LoadStatus;
  method?: SterilizationMethod;
  biologicalPending?: boolean;
  from?: string;
  to?: string;
  search?: string;
  page?: number;
}

export interface IssueFilters {
  departmentId?: string;
  loadId?: string;
  openRecalls?: boolean;
  page?: number;
  pageSize?: number;
}

export const cssdKeys = {
  all: ['cssd'] as const,
  sets: (includeInactive: boolean) => ['cssd', 'sets', includeInactive] as const,
  requests: (filters: { status?: CssdRequestStatus; departmentId?: string; openOnly?: boolean }) =>
    ['cssd', 'requests', filters] as const,
  receipts: (filters: { from?: string; to?: string; discrepanciesOnly?: boolean }) =>
    ['cssd', 'receipts', filters] as const,
  packs: (filters: { setId?: string; status?: PackStatus; expiringWithinDays?: number }) =>
    ['cssd', 'packs', filters] as const,
  issues: (filters: IssueFilters) => ['cssd', 'issues', filters] as const,
  summary: (from: string, to: string) => ['cssd', 'summary', from, to] as const,
  loads: (filters: LoadFilters) => ['cssd', 'loads', filters] as const,
  load: (id: string) => ['cssd', 'load', id] as const,
  recall: (id: string) => ['cssd', 'load', id, 'recall'] as const,
};

export const cssdQueries = {
  sets: (includeInactive = false) =>
    queryOptions({
      queryKey: cssdKeys.sets(includeInactive),
      queryFn: ({ signal }) =>
        api.get<InstrumentSetDto[]>('/api/v1/cssd/sets', {
          query: { includeInactive: includeInactive || undefined },
          signal,
        }),
      staleTime: 60_000,
    }),
  requests: (filters: { status?: CssdRequestStatus; departmentId?: string; openOnly?: boolean }) =>
    queryOptions({
      queryKey: cssdKeys.requests(filters),
      queryFn: ({ signal }) => api.get<CssdRequestDto[]>('/api/v1/cssd/requests', { query: { ...filters }, signal }),
      refetchInterval: 60_000,
    }),
  receipts: (filters: { from?: string; to?: string; discrepanciesOnly?: boolean }) =>
    queryOptions({
      queryKey: cssdKeys.receipts(filters),
      queryFn: ({ signal }) => api.get<SoiledReceiptDto[]>('/api/v1/cssd/receipts', { query: { ...filters }, signal }),
    }),
  packs: (filters: { setId?: string; status?: PackStatus; expiringWithinDays?: number }) =>
    queryOptions({
      queryKey: cssdKeys.packs(filters),
      queryFn: ({ signal }) => api.get<SterilePackDto[]>('/api/v1/cssd/packs', { query: { ...filters }, signal }),
    }),
  issues: (filters: IssueFilters) =>
    queryOptions({
      queryKey: cssdKeys.issues(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<SetIssueDto>>('/api/v1/cssd/issues', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  summary: (from: string, to: string) =>
    queryOptions({
      queryKey: cssdKeys.summary(from, to),
      queryFn: ({ signal }) => api.get<CssdSummary>('/api/v1/cssd/reports/summary', { query: { from, to }, signal }),
      refetchInterval: 120_000,
    }),
  loads: (filters: LoadFilters) =>
    queryOptions({
      queryKey: cssdKeys.loads(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<LoadListItem>>('/api/v1/cssd/loads', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  load: (id: string) =>
    queryOptions({
      queryKey: cssdKeys.load(id),
      queryFn: ({ signal }) => api.get<LoadDto>(`/api/v1/cssd/loads/${id}`, { signal }),
    }),
  recall: (id: string) =>
    queryOptions({
      queryKey: cssdKeys.recall(id),
      queryFn: ({ signal }) => api.get<RecallReport>(`/api/v1/cssd/loads/${id}/recall`, { signal }),
    }),
};

export const cssdApi = {
  createSet: (body: SaveInstrumentSetRequest) => api.post<InstrumentSetDto>('/api/v1/cssd/sets', body),
  updateSet: (id: string, body: SaveInstrumentSetRequest) => api.put<void>(`/api/v1/cssd/sets/${id}`, body),
  createRequest: (body: CreateCssdRequestRequest) => api.post<CssdRequestDto>('/api/v1/cssd/requests', body),
  cancelRequest: (id: string, reason: string) => api.post<void>(`/api/v1/cssd/requests/${id}/cancel`, { reason }),
  receiveSoiled: (body: ReceiveSoiledRequest) => api.post<SoiledReceiptDto>('/api/v1/cssd/receipts', body),
  issue: (body: IssuePacksRequest) => api.post<SetIssueDto[]>('/api/v1/cssd/issues', body),
  recordUse: (issueId: string, body: { patientId: string; usedAt: string | null }) =>
    api.post<void>(`/api/v1/cssd/issues/${issueId}/use`, body),
  acknowledgeRecall: (issueId: string, action: string) =>
    api.post<void>(`/api/v1/cssd/issues/${issueId}/acknowledge-recall`, { action }),
  createLoad: (body: CreateLoadRequest) => api.post<LoadDto>('/api/v1/cssd/loads', body),
  startLoad: (id: string) => api.post<LoadDto>(`/api/v1/cssd/loads/${id}/start`),
  completeLoad: (
    id: string,
    body: {
      temperatureC: number;
      pressureKpa: number | null;
      exposureMinutes: number;
      chemicalIndicator: 'Pass' | 'Fail';
    },
  ) => api.post<LoadDto>(`/api/v1/cssd/loads/${id}/complete`, body),
  releaseLoad: (id: string) => api.post<LoadDto>(`/api/v1/cssd/loads/${id}/release`),
  recordBiological: (id: string, body: { result: 'Pass' | 'Fail'; lot: string | null }) =>
    api.post<LoadDto>(`/api/v1/cssd/loads/${id}/biological-indicator`, body),
  abortLoad: (id: string, reason: string) => api.post<LoadDto>(`/api/v1/cssd/loads/${id}/abort`, { reason }),
};
