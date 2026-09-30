import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DocumentCreated } from '@/modules/inventory/api';
import type {
  AssetCategory,
  AssetCriticality,
  AssetStatus,
  CalibrationResult,
  CondemnationStatus,
  DepreciationMethod,
  MaintenanceContractType,
  PmVisitOutcome,
  TicketPriority,
  TicketStatus,
} from './labels';

export interface AssetListItem {
  id: string;
  assetTag: string;
  name: string;
  category: AssetCategory;
  make: string | null;
  model: string | null;
  serialNumber: string | null;
  departmentId: string | null;
  departmentName: string | null;
  location: string | null;
  status: AssetStatus;
  criticality: AssetCriticality;
  warrantyExpiry: string | null;
  nextPmDue: string | null;
  nextCalibrationDue: string | null;
  openTickets: number;
}

export interface AssetDetail {
  id: string;
  assetTag: string;
  barcode: string | null;
  name: string;
  category: AssetCategory;
  make: string | null;
  model: string | null;
  serialNumber: string | null;
  departmentId: string | null;
  departmentName: string | null;
  location: string | null;
  custodianUserId: string | null;
  custodianName: string | null;
  purchaseDate: string | null;
  purchaseCost: number;
  vendorId: string | null;
  vendorName: string | null;
  purchaseOrderNumber: string | null;
  invoiceNumber: string | null;
  installationDate: string | null;
  warrantyExpiry: string | null;
  underWarranty: boolean;
  status: AssetStatus;
  criticality: AssetCriticality;
  depreciationMethod: DepreciationMethod;
  usefulLifeYears: number;
  salvageValue: number;
  depreciationRatePercent: number;
  bookValue: number;
  requiresCalibration: boolean;
  nextCalibrationDue: string | null;
  condemnedOn: string | null;
  disposedOn: string | null;
  disposalValue: number | null;
  disposalMethod: string | null;
  notes: string | null;
  version: number;
}

export interface SaveAssetRequest {
  name: string;
  category: AssetCategory;
  make: string | null;
  model: string | null;
  serialNumber: string | null;
  departmentId: string | null;
  location: string | null;
  custodianUserId: string | null;
  custodianName: string | null;
  purchaseDate: string | null;
  purchaseCost: number;
  vendorId: string | null;
  purchaseOrderNumber: string | null;
  invoiceNumber: string | null;
  installationDate: string | null;
  warrantyExpiry: string | null;
  criticality: AssetCriticality;
  depreciationMethod: DepreciationMethod;
  usefulLifeYears: number;
  salvageValue: number;
  depreciationRatePercent: number | null;
  requiresCalibration: boolean;
  barcode: string | null;
  notes: string | null;
  version: number | null;
}

export interface AssetContractDto {
  id: string;
  assetId: string;
  type: MaintenanceContractType;
  vendorId: string | null;
  vendorName: string | null;
  contractNumber: string | null;
  startDate: string;
  endDate: string;
  cost: number;
  coverageNotes: string | null;
  isCurrent: boolean;
  version: number;
}

export interface SaveAssetContractRequest {
  type: MaintenanceContractType;
  vendorId: string | null;
  contractNumber: string | null;
  startDate: string;
  endDate: string;
  cost: number;
  coverageNotes: string | null;
  version: number | null;
}

export interface DepreciationYear {
  year: number;
  from: string;
  to: string;
  opening: number;
  depreciation: number;
  closing: number;
}

export interface AssetDepreciation {
  assetId: string;
  method: DepreciationMethod;
  cost: number;
  salvageValue: number;
  ratePercent: number;
  startDate: string | null;
  asOf: string;
  bookValue: number;
  accumulatedDepreciation: number;
  schedule: DepreciationYear[];
}

export interface CondemnationDto {
  id: string;
  assetId: string;
  assetTag: string;
  assetName: string;
  reason: string;
  bookValueAtRequest: number;
  status: CondemnationStatus;
  requestedAt: string;
  requestedBy: string | null;
  decidedAt: string | null;
  decisionRemarks: string | null;
}

export interface AssetDashboard {
  totalAssets: number;
  byStatus: { status: AssetStatus; count: number }[];
  openTickets: number;
  criticalOpenTickets: number;
  pmDueThisWeek: number;
  pmOverdue: number;
  calibrationDue: number;
  warrantyExpiring: number;
  contractsExpiring: number;
  downtimeHoursThisMonth: number;
  bookValue: number;
}

export interface PmPlanDto {
  id: string;
  assetId: string;
  assetTag: string;
  assetName: string;
  title: string;
  frequencyDays: number;
  nextDueDate: string;
  lastDoneOn: string | null;
  checklist: string | null;
  assignedTo: string | null;
  vendorId: string | null;
  isActive: boolean;
  daysToDue: number;
  version: number;
}

export interface SavePmPlanRequest {
  title: string;
  frequencyDays: number;
  nextDueDate: string;
  checklist: string | null;
  assignedTo: string | null;
  vendorId: string | null;
  isActive: boolean;
  version: number | null;
}

export interface PmVisitDto {
  id: string;
  planId: string;
  assetId: string;
  visitDate: string;
  performedBy: string | null;
  outcome: PmVisitOutcome;
  findings: string | null;
  cost: number;
  nextDueDate: string;
  serviceReportFileId: string | null;
}

export interface RecordPmVisitRequest {
  visitDate: string;
  performedBy: string | null;
  vendorId: string | null;
  outcome: PmVisitOutcome;
  findings: string | null;
  cost: number;
  serviceReportFileId: string | null;
}

export interface TicketListItem {
  id: string;
  number: string;
  assetId: string;
  assetTag: string;
  assetName: string;
  assetCriticality: AssetCriticality;
  priority: TicketPriority;
  status: TicketStatus;
  issue: string;
  reportedAt: string;
  reportedBy: string | null;
  assignedTo: string | null;
  resolvedAt: string | null;
  downtimeMinutes: number | null;
}

export interface TicketDetail {
  id: string;
  number: string;
  assetId: string;
  assetTag: string;
  assetName: string;
  priority: TicketPriority;
  status: TicketStatus;
  issue: string;
  reportedAt: string;
  reportedBy: string | null;
  assignedTo: string | null;
  assignedToUserId: string | null;
  vendorId: string | null;
  assignedAt: string | null;
  startedAt: string | null;
  resolvedAt: string | null;
  closedAt: string | null;
  resolution: string | null;
  rootCause: string | null;
  cost: number;
  downtimeMinutes: number | null;
  version: number;
}

export interface ReportBreakdownRequest {
  assetId: string;
  issue: string;
  priority: TicketPriority | null;
  assetDown: boolean;
}

export interface ResolveTicketRequest {
  resolution: string;
  rootCause: string | null;
  cost: number;
  downtimeMinutes: number | null;
}

export interface CalibrationDto {
  id: string;
  assetId: string;
  calibratedOn: string;
  agency: string;
  certificateNumber: string | null;
  certificateFileId: string | null;
  nextDueDate: string;
  outcome: CalibrationResult;
  remarks: string | null;
}

export interface RecordCalibrationRequest {
  calibratedOn: string;
  agency: string;
  certificateNumber: string | null;
  certificateFileId: string | null;
  nextDueDate: string;
  outcome: CalibrationResult;
  remarks: string | null;
}

export interface MaintenanceDueItem {
  kind: string;
  facilityId: string;
  assetId: string;
  assetTag: string;
  assetName: string;
  title: string;
  dueDate: string;
  daysToDue: number;
}

export interface AssetFilters {
  search?: string;
  category?: AssetCategory;
  status?: AssetStatus;
  criticality?: AssetCriticality;
  departmentId?: string;
  includeRetired?: boolean;
  page?: number;
}

const BASE = '/api/v1/assets';
export const PAGE_SIZE = 25;

export const assetKeys = { all: ['assets'] as const };

export const assetQueries = {
  list: (filters: AssetFilters) =>
    queryOptions({
      queryKey: ['assets', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<AssetListItem>>(BASE, { query: { ...filters, pageSize: PAGE_SIZE }, signal }),
      placeholderData: keepPreviousData,
    }),
  search: (search: string) =>
    queryOptions({
      queryKey: ['assets', 'search', search],
      queryFn: ({ signal }) => api.get<PagedResult<AssetListItem>>(BASE, { query: { search, pageSize: 10 }, signal }),
      staleTime: 30_000,
    }),
  dashboard: () =>
    queryOptions({
      queryKey: ['assets', 'dashboard'],
      queryFn: ({ signal }) => api.get<AssetDashboard>(`${BASE}/dashboard`, { signal }),
      refetchInterval: 120_000,
    }),
  detail: (id: string) =>
    queryOptions({
      queryKey: ['assets', 'detail', id],
      queryFn: ({ signal }) => api.get<AssetDetail>(`${BASE}/${id}`, { signal }),
    }),
  contracts: (id: string) =>
    queryOptions({
      queryKey: ['assets', 'contracts', id],
      queryFn: ({ signal }) => api.get<AssetContractDto[]>(`${BASE}/${id}/contracts`, { signal }),
    }),
  depreciation: (id: string, asOf?: string) =>
    queryOptions({
      queryKey: ['assets', 'depreciation', id, asOf ?? null],
      queryFn: ({ signal }) => api.get<AssetDepreciation>(`${BASE}/${id}/depreciation`, { query: { asOf }, signal }),
    }),
  condemnations: (status?: CondemnationStatus) =>
    queryOptions({
      queryKey: ['assets', 'condemnations', status ?? 'all'],
      queryFn: ({ signal }) => api.get<CondemnationDto[]>(`${BASE}/condemnations`, { query: { status }, signal }),
    }),
  due: (days = 30) =>
    queryOptions({
      queryKey: ['assets', 'due', days],
      queryFn: ({ signal }) => api.get<MaintenanceDueItem[]>(`${BASE}/maintenance/due`, { query: { days }, signal }),
      refetchInterval: 300_000,
    }),
  plans: (assetId?: string, dueOnly?: boolean) =>
    queryOptions({
      queryKey: ['assets', 'pm-plans', assetId ?? 'all', dueOnly ?? false],
      queryFn: ({ signal }) => api.get<PmPlanDto[]>(`${BASE}/pm-plans`, { query: { assetId, dueOnly }, signal }),
    }),
  visits: (assetId: string) =>
    queryOptions({
      queryKey: ['assets', 'pm-visits', assetId],
      queryFn: ({ signal }) => api.get<PmVisitDto[]>(`${BASE}/${assetId}/pm-visits`, { signal }),
    }),
  tickets: (filters: {
    status?: TicketStatus;
    priority?: TicketPriority;
    assetId?: string;
    openOnly?: boolean;
    search?: string;
    page?: number;
  }) =>
    queryOptions({
      queryKey: ['assets', 'tickets', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<TicketListItem>>(`${BASE}/tickets`, { query: { ...filters, pageSize: PAGE_SIZE }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  ticket: (id: string) =>
    queryOptions({
      queryKey: ['assets', 'ticket', id],
      queryFn: ({ signal }) => api.get<TicketDetail>(`${BASE}/tickets/${id}`, { signal }),
    }),
  calibrations: (assetId: string) =>
    queryOptions({
      queryKey: ['assets', 'calibrations', assetId],
      queryFn: ({ signal }) => api.get<CalibrationDto[]>(`${BASE}/${assetId}/calibrations`, { signal }),
    }),
};

export const assetApi = {
  /** Creates (id null, returns the new asset) or updates an asset (returns null). */
  save: async (id: string | null, body: SaveAssetRequest): Promise<DocumentCreated | null> =>
    id ? (await api.put<void>(`${BASE}/${id}`, body), null) : api.post<DocumentCreated>(BASE, body),
  byTag: (tag: string) => api.get<AssetDetail>(`${BASE}/by-tag/${encodeURIComponent(tag)}`),
  setOperational: (id: string, inUse: boolean) => api.post<void>(`${BASE}/${id}/operational`, { inUse }),
  saveContract: async (assetId: string, contractId: string | null, body: SaveAssetContractRequest): Promise<void> => {
    if (contractId) {
      await api.put<void>(`${BASE}/${assetId}/contracts/${contractId}`, body);
    } else {
      await api.post<unknown>(`${BASE}/${assetId}/contracts`, body);
    }
  },
  requestCondemnation: (assetId: string, reason: string) =>
    api.post<unknown>(`${BASE}/${assetId}/condemnation`, { reason }),
  approveCondemnation: (id: string, remarks: string | null) =>
    api.post<void>(`${BASE}/condemnations/${id}/approve`, { remarks }),
  rejectCondemnation: (id: string, remarks: string | null) =>
    api.post<void>(`${BASE}/condemnations/${id}/reject`, { remarks }),
  dispose: (assetId: string, body: { disposedOn: string; disposalValue: number; method: string }) =>
    api.post<void>(`${BASE}/${assetId}/dispose`, body),
  savePlan: async (assetId: string, planId: string | null, body: SavePmPlanRequest): Promise<void> => {
    if (planId) {
      await api.put<void>(`${BASE}/${assetId}/pm-plans/${planId}`, body);
    } else {
      await api.post<unknown>(`${BASE}/${assetId}/pm-plans`, body);
    }
  },
  recordVisit: (planId: string, body: RecordPmVisitRequest) =>
    api.post<unknown>(`${BASE}/pm-plans/${planId}/visits`, body),
  reportBreakdown: (body: ReportBreakdownRequest) => api.post<DocumentCreated>(`${BASE}/tickets`, body),
  assignTicket: (id: string, body: { assignedTo: string; assignedToUserId: string | null; vendorId: string | null }) =>
    api.post<void>(`${BASE}/tickets/${id}/assign`, body),
  startTicket: (id: string) => api.post<void>(`${BASE}/tickets/${id}/start`),
  resolveTicket: (id: string, body: ResolveTicketRequest) => api.post<void>(`${BASE}/tickets/${id}/resolve`, body),
  closeTicket: (id: string) => api.post<void>(`${BASE}/tickets/${id}/close`),
  reopenTicket: (id: string) => api.post<void>(`${BASE}/tickets/${id}/reopen`),
  recordCalibration: (assetId: string, body: RecordCalibrationRequest) =>
    api.post<unknown>(`${BASE}/${assetId}/calibrations`, body),
};
