import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  CapaSource,
  CapaStatus,
  CapaType,
  ContributingFactorCategory,
  IncidentSeverity,
  IncidentStatus,
  IncidentType,
  IndicatorDirection,
  IndicatorFrequency,
  IndicatorUnit,
  InvestigationStatus,
  RcaMethod,
  RiskCategory,
  RiskRating,
  RiskStatus,
  SentinelEventKind,
  TrendDirection,
} from './labels';

// ------------------------------------------------------------------ incidents

export interface ReportIncidentRequest {
  type: IncidentType;
  severity: IncidentSeverity;
  occurredAt: string;
  location: string;
  departmentId: string | null;
  patientId: string | null;
  involvedStaffUserIds: string[] | null;
  description: string;
  immediateAction: string | null;
  witnesses: string | null;
  sentinelKind: SentinelEventKind | null;
  flagAsSentinel: boolean;
  isAnonymous: boolean;
}

export interface IncidentReceipt {
  id: string;
  incidentNumber: string;
  isSentinel: boolean;
  severity: IncidentSeverity;
  type: IncidentType;
}

export interface IncidentListItem {
  id: string;
  incidentNumber: string;
  type: IncidentType;
  severity: IncidentSeverity;
  isSentinel: boolean;
  status: IncidentStatus;
  occurredAt: string;
  reportedAt: string;
  location: string;
  departmentId: string | null;
  departmentName: string | null;
  isAnonymous: boolean;
  assignedToUserId: string | null;
  openCapas: number;
}

export interface MyIncidentItem {
  id: string;
  incidentNumber: string;
  type: IncidentType;
  severity: IncidentSeverity;
  status: IncidentStatus;
  reportedAt: string;
}

export interface PersonRef {
  id: string;
  name: string;
}

export interface IncidentCapaItem {
  id: string;
  capaNumber: string;
  type: CapaType;
  action: string;
  status: CapaStatus;
  dueDate: string;
  isOverdue: boolean;
}

export interface IncidentDetail {
  id: string;
  incidentNumber: string;
  type: IncidentType;
  severity: IncidentSeverity;
  isSentinel: boolean;
  sentinelKind: SentinelEventKind | null;
  sentinelReason: string | null;
  status: IncidentStatus;
  nextStatuses: IncidentStatus[];
  occurredAt: string;
  reportedAt: string;
  location: string;
  departmentId: string | null;
  departmentName: string | null;
  patientId: string | null;
  involvedStaff: PersonRef[];
  description: string;
  immediateAction: string | null;
  witnesses: string | null;
  isAnonymous: boolean;
  reporterName: string | null;
  assignedTo: PersonRef | null;
  closedAt: string | null;
  closureNote: string | null;
  lastStatusNote: string | null;
  reopenCount: number;
  investigationStatus: InvestigationStatus | null;
  capas: IncidentCapaItem[];
  version: number;
}

export interface UpdateIncidentRequest {
  type: IncidentType;
  severity: IncidentSeverity;
  sentinelKind: SentinelEventKind | null;
  flagAsSentinel: boolean;
  location: string;
  departmentId: string | null;
  patientId: string | null;
  involvedStaffUserIds: string[] | null;
  version: number;
}

export interface RcaFactorInput {
  category: ContributingFactorCategory;
  description: string;
  isRootCause: boolean;
}

export interface SaveInvestigationRequest {
  method: RcaMethod;
  problemStatement: string;
  whys: string[] | null;
  factors: RcaFactorInput[] | null;
  findings: string | null;
  recommendations: string | null;
  investigatorUserIds: string[] | null;
}

export interface RcaFactorDto extends RcaFactorInput {
  id: string;
}

export interface InvestigationDto {
  id: string;
  incidentId: string;
  method: RcaMethod;
  problemStatement: string;
  whys: string[];
  factors: RcaFactorDto[];
  findings: string | null;
  recommendations: string | null;
  investigators: PersonRef[];
  status: InvestigationStatus;
  startedAt: string;
  completedAt: string | null;
  version: number;
}

export interface IncidentFilters {
  status?: IncidentStatus;
  type?: IncidentType;
  severity?: IncidentSeverity;
  sentinelOnly?: boolean;
  openOnly?: boolean;
  departmentId?: string;
  from?: string;
  to?: string;
  search?: string;
  sort?: string;
  desc?: boolean;
  page?: number;
}

// ------------------------------------------------------------------ CAPA

export interface CapaListItem {
  id: string;
  capaNumber: string;
  source: CapaSource;
  incidentId: string | null;
  incidentNumber: string | null;
  riskId: string | null;
  type: CapaType;
  action: string;
  ownerUserId: string;
  ownerName: string | null;
  dueDate: string;
  status: CapaStatus;
  isOverdue: boolean;
  effectivenessCheckDue: string | null;
}

export interface CapaDetail {
  id: string;
  capaNumber: string;
  source: CapaSource;
  incidentId: string | null;
  riskId: string | null;
  type: CapaType;
  action: string;
  ownerUserId: string;
  ownerName: string | null;
  dueDate: string;
  status: CapaStatus;
  isOverdue: boolean;
  startedAt: string | null;
  completedAt: string | null;
  completionNote: string | null;
  effectivenessCheckDue: string | null;
  isEffective: boolean | null;
  effectivenessNote: string | null;
  verifiedAt: string | null;
  cancelReason: string | null;
  reworkCount: number;
  version: number;
}

export interface CreateCapaRequest {
  source: CapaSource;
  incidentId: string | null;
  riskId: string | null;
  type: CapaType;
  action: string;
  ownerUserId: string;
  dueDate: string;
}

export interface UpdateCapaRequest {
  action: string;
  ownerUserId: string;
  dueDate: string;
  effectivenessCheckDue: string | null;
  version: number;
}

export interface CapaFilters {
  status?: CapaStatus;
  openOnly?: boolean;
  overdueOnly?: boolean;
  ownerUserId?: string;
  incidentId?: string;
  source?: CapaSource;
  search?: string;
  page?: number;
}

// ------------------------------------------------------------------ risks

export interface RiskListItem {
  id: string;
  riskNumber: string;
  title: string;
  category: RiskCategory;
  departmentId: string | null;
  likelihood: number;
  impact: number;
  score: number;
  rating: RiskRating;
  residualScore: number | null;
  residualRating: RiskRating | null;
  status: RiskStatus;
  ownerUserId: string | null;
  reviewDate: string;
  isReviewOverdue: boolean;
}

export interface RiskDetail {
  id: string;
  riskNumber: string;
  title: string;
  description: string | null;
  category: RiskCategory;
  departmentId: string | null;
  likelihood: number;
  impact: number;
  score: number;
  rating: RiskRating;
  residualLikelihood: number | null;
  residualImpact: number | null;
  residualScore: number | null;
  residualRating: RiskRating | null;
  existingControls: string | null;
  mitigationPlan: string | null;
  ownerUserId: string | null;
  ownerName: string | null;
  reviewDate: string;
  lastReviewedAt: string | null;
  lastReviewNote: string | null;
  status: RiskStatus;
  isReviewOverdue: boolean;
  version: number;
}

export interface SaveRiskRequest {
  title: string;
  description: string | null;
  category: RiskCategory;
  departmentId: string | null;
  likelihood: number;
  impact: number;
  residualLikelihood: number | null;
  residualImpact: number | null;
  existingControls: string | null;
  mitigationPlan: string | null;
  ownerUserId: string | null;
  reviewDate: string;
  status: RiskStatus;
  version: number | null;
}

export interface ReviewRiskRequest {
  likelihood: number;
  impact: number;
  residualLikelihood: number | null;
  residualImpact: number | null;
  existingControls: string | null;
  nextReviewDate: string;
  note: string | null;
  version: number;
}

export interface RiskMatrixCell {
  likelihood: number;
  impact: number;
  score: number;
  rating: RiskRating;
  count: number;
}

export interface RiskFilters {
  category?: RiskCategory;
  rating?: RiskRating;
  status?: RiskStatus;
  departmentId?: string;
  reviewOverdue?: boolean;
  search?: string;
  sort?: string;
  desc?: boolean;
  page?: number;
  pageSize?: number;
}

// ------------------------------------------------------------------ indicators

export interface IndicatorLatest {
  periodStart: string;
  periodLabel: string;
  value: number;
  meetsTarget: boolean | null;
}

export interface IndicatorDto {
  id: string;
  code: string;
  name: string;
  category: string;
  numeratorDefinition: string;
  denominatorDefinition: string;
  unit: IndicatorUnit;
  multiplier: number;
  target: number | null;
  direction: IndicatorDirection;
  frequency: IndicatorFrequency;
  nabhReference: string | null;
  sortOrder: number;
  isActive: boolean;
  latest: IndicatorLatest | null;
  version: number;
}

export interface SaveIndicatorRequest {
  code: string;
  name: string;
  category: string;
  numeratorDefinition: string;
  denominatorDefinition: string;
  unit: IndicatorUnit;
  multiplier: number | null;
  target: number | null;
  direction: IndicatorDirection;
  frequency: IndicatorFrequency;
  nabhReference: string | null;
  sortOrder: number;
  isActive: boolean;
  version: number | null;
}

export interface IndicatorValueDto {
  id: string;
  periodStart: string;
  periodLabel: string;
  numerator: number;
  denominator: number;
  value: number;
  meetsTarget: boolean | null;
  notes: string | null;
}

export interface IndicatorTrendDto {
  indicatorId: string;
  code: string;
  name: string;
  unit: IndicatorUnit;
  target: number | null;
  direction: IndicatorDirection;
  points: IndicatorValueDto[];
  latest: number | null;
  average: number | null;
  trend: TrendDirection;
}

export interface ScorecardRow {
  indicatorId: string;
  code: string;
  name: string;
  category: string;
  unit: IndicatorUnit;
  target: number | null;
  direction: IndicatorDirection;
  periodStart: string;
  periodLabel: string;
  value: number | null;
  meetsTarget: boolean | null;
}

// ------------------------------------------------------------------ dashboard

export interface CountByKey {
  key: string;
  count: number;
}

export interface QualityDashboard {
  from: string;
  to: string;
  totalIncidents: number;
  sentinelEvents: number;
  openIncidents: number;
  byType: CountByKey[];
  bySeverity: CountByKey[];
  openByStatus: CountByKey[];
  byMonth: { month: string; total: number; sentinel: number }[];
  capas: { open: number; inProgress: number; awaitingVerification: number; overdue: number; verifiedInPeriod: number };
  overdueCapas: { id: string; capaNumber: string; action: string; ownerName: string | null; dueDate: string; daysOverdue: number }[];
  topRisks: { id: string; riskNumber: string; title: string; score: number; rating: RiskRating; residualScore: number | null; reviewDate: string }[];
  risksDueForReview: number;
  indicatorsOffTarget: { id: string; code: string; name: string; periodLabel: string; value: number; target: number | null; direction: IndicatorDirection }[];
}

// ------------------------------------------------------------------ queries

export const qualityKeys = {
  all: ['quality'] as const,
  incidents: ['quality', 'incidents'] as const,
  capas: ['quality', 'capas'] as const,
  risks: ['quality', 'risks'] as const,
  indicators: ['quality', 'indicators'] as const,
  dashboard: ['quality', 'dashboard'] as const,
};

export const qualityQueries = {
  incidents: (filters: IncidentFilters) =>
    queryOptions({
      queryKey: ['quality', 'incidents', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<IncidentListItem>>('/api/v1/quality/incidents', { query: { ...filters, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  myIncidents: () =>
    queryOptions({
      queryKey: ['quality', 'incidents', 'mine'],
      queryFn: ({ signal }) => api.get<MyIncidentItem[]>('/api/v1/quality/incidents/mine', { signal }),
    }),
  incident: (id: string) =>
    queryOptions({
      queryKey: ['quality', 'incidents', 'detail', id],
      queryFn: ({ signal }) => api.get<IncidentDetail>(`/api/v1/quality/incidents/${id}`, { signal }),
    }),
  investigation: (incidentId: string) =>
    queryOptions({
      queryKey: ['quality', 'incidents', 'investigation', incidentId],
      queryFn: ({ signal }) => api.get<InvestigationDto>(`/api/v1/quality/incidents/${incidentId}/investigation`, { signal }),
      retry: false,
    }),
  capas: (filters: CapaFilters) =>
    queryOptions({
      queryKey: ['quality', 'capas', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<CapaListItem>>('/api/v1/quality/capas', { query: { ...filters, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  myCapas: (includeClosed: boolean) =>
    queryOptions({
      queryKey: ['quality', 'capas', 'mine', includeClosed],
      queryFn: ({ signal }) => api.get<CapaListItem[]>('/api/v1/quality/capas/mine', { query: { includeClosed }, signal }),
    }),
  capa: (id: string) =>
    queryOptions({
      queryKey: ['quality', 'capas', 'detail', id],
      queryFn: ({ signal }) => api.get<CapaDetail>(`/api/v1/quality/capas/${id}`, { signal }),
    }),
  risks: (filters: RiskFilters) =>
    queryOptions({
      queryKey: ['quality', 'risks', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<RiskListItem>>('/api/v1/quality/risks', { query: { ...filters, pageSize: filters.pageSize ?? 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  riskMatrix: (residual: boolean) =>
    queryOptions({
      queryKey: ['quality', 'risks', 'matrix', residual],
      queryFn: ({ signal }) => api.get<RiskMatrixCell[]>('/api/v1/quality/risks/matrix', { query: { residual }, signal }),
    }),
  risk: (id: string) =>
    queryOptions({
      queryKey: ['quality', 'risks', 'detail', id],
      queryFn: ({ signal }) => api.get<RiskDetail>(`/api/v1/quality/risks/${id}`, { signal }),
    }),
  indicators: (includeInactive = false, category?: string) =>
    queryOptions({
      queryKey: ['quality', 'indicators', 'list', includeInactive, category ?? 'all'],
      queryFn: ({ signal }) => api.get<IndicatorDto[]>('/api/v1/quality/indicators', { query: { includeInactive, category }, signal }),
    }),
  indicatorValues: (id: string, from?: string, to?: string) =>
    queryOptions({
      queryKey: ['quality', 'indicators', 'values', id, from ?? '', to ?? ''],
      queryFn: ({ signal }) => api.get<IndicatorValueDto[]>(`/api/v1/quality/indicators/${id}/values`, { query: { from, to }, signal }),
    }),
  indicatorTrend: (id: string, from?: string, to?: string) =>
    queryOptions({
      queryKey: ['quality', 'indicators', 'trend', id, from ?? '', to ?? ''],
      queryFn: ({ signal }) => api.get<IndicatorTrendDto>(`/api/v1/quality/indicators/${id}/trend`, { query: { from, to }, signal }),
    }),
  scorecard: (period?: string) =>
    queryOptions({
      queryKey: ['quality', 'indicators', 'scorecard', period ?? 'last'],
      queryFn: ({ signal }) => api.get<ScorecardRow[]>('/api/v1/quality/indicators/scorecard', { query: { period }, signal }),
    }),
  dashboard: (from?: string, to?: string) =>
    queryOptions({
      queryKey: ['quality', 'dashboard', from ?? '', to ?? ''],
      queryFn: ({ signal }) => api.get<QualityDashboard>('/api/v1/quality/dashboard', { query: { from, to }, signal }),
      refetchInterval: 5 * 60_000,
    }),
};

// ------------------------------------------------------------------ commands

export const qualityApi = {
  report: (request: ReportIncidentRequest) => api.post<IncidentReceipt>('/api/v1/quality/incidents', request),
  updateIncident: (id: string, request: UpdateIncidentRequest) => api.put<void>(`/api/v1/quality/incidents/${id}`, request),
  transition: (id: string, status: IncidentStatus, note: string | null) => api.post<void>(`/api/v1/quality/incidents/${id}/transition`, { status, note }),
  assign: (id: string, userId: string) => api.post<void>(`/api/v1/quality/incidents/${id}/assign`, { userId }),
  saveInvestigation: (incidentId: string, request: SaveInvestigationRequest) => api.put<string>(`/api/v1/quality/incidents/${incidentId}/investigation`, request),
  completeInvestigation: (incidentId: string) => api.post<void>(`/api/v1/quality/incidents/${incidentId}/investigation/complete`),
  reopenInvestigation: (incidentId: string) => api.post<void>(`/api/v1/quality/incidents/${incidentId}/investigation/reopen`),

  createCapa: (request: CreateCapaRequest) => api.post<string>('/api/v1/quality/capas', request),
  updateCapa: (id: string, request: UpdateCapaRequest) => api.put<void>(`/api/v1/quality/capas/${id}`, request),
  startCapa: (id: string) => api.post<void>(`/api/v1/quality/capas/${id}/start`),
  completeCapa: (id: string, note: string) => api.post<void>(`/api/v1/quality/capas/${id}/complete`, { note }),
  verifyCapa: (id: string, effective: boolean, note: string) => api.post<void>(`/api/v1/quality/capas/${id}/verify`, { effective, note }),
  cancelCapa: (id: string, reason: string) => api.post<void>(`/api/v1/quality/capas/${id}/cancel`, { reason }),

  createRisk: (request: SaveRiskRequest) => api.post<{ id: string; riskNumber: string; score: number; rating: RiskRating }>('/api/v1/quality/risks', request),
  updateRisk: (id: string, request: SaveRiskRequest) => api.put<void>(`/api/v1/quality/risks/${id}`, request),
  reviewRisk: (id: string, request: ReviewRiskRequest) => api.post<void>(`/api/v1/quality/risks/${id}/review`, request),

  saveIndicator: (id: string | null, request: SaveIndicatorRequest) =>
    id ? api.put<void>(`/api/v1/quality/indicators/${id}`, request) : api.post<unknown>('/api/v1/quality/indicators', request),
  recordValue: (id: string, request: { periodStart: string; numerator: number; denominator: number; notes: string | null }) =>
    api.put<IndicatorValueDto>(`/api/v1/quality/indicators/${id}/values`, request),
  deleteValue: (valueId: string) => api.delete<void>(`/api/v1/quality/indicators/values/${valueId}`),
};
