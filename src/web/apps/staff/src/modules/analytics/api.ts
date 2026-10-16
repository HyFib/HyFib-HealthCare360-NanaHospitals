import type { LookupItem, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Analytics API (docs/modules/analytics.md): the report catalogue, report runs and exports, executive KPIs, saved
 * dashboards, scheduled report e-mails and BI extracts. Types mirror the server records (camelCase; enums as strings;
 * DateOnly as "yyyy-MM-dd"; money as rupees).
 */
const BASE = '/api/v1/analytics';

// ------------------------------------------------------------------------------------------------ catalogue

export type ReportCategory =
  | 'OutPatient'
  | 'InPatient'
  | 'Revenue'
  | 'Diagnostics'
  | 'Pharmacy'
  | 'SpecialtyCare'
  | 'Quality'
  | 'Experience'
  | 'People';
export type ReportValueType =
  'Text' | 'Integer' | 'Decimal' | 'Money' | 'Percent' | 'Days' | 'Minutes' | 'Date' | 'DateTime' | 'Month';
export type ReportParameterKind =
  'Period' | 'Facility' | 'Department' | 'Practitioner' | 'Payer' | 'Ward' | 'Store' | 'Theatre' | 'Choice' | 'Number';
export type ReportChartType = 'Bar' | 'StackedBar' | 'Line' | 'Area' | 'Pie' | 'Table' | 'Summary';

export interface ReportChoice {
  value: string;
  label: string;
}

export interface ReportParameter {
  key: string;
  label: string;
  kind: ReportParameterKind;
  required: boolean;
  defaultValue: string | null;
  choices: ReportChoice[] | null;
  min: number | null;
  max: number | null;
  hint: string | null;
}

export interface ReportColumn {
  key: string;
  label: string;
  type: ReportValueType;
  isPatientIdentifier: boolean;
}

export interface ReportChartHint {
  type: ReportChartType;
  categoryKey: string;
  seriesKeys: string[];
  timeSeries: boolean;
  topN: number;
}

export interface ReportCatalogueItem {
  id: string;
  title: string;
  category: ReportCategory;
  categoryLabel: string;
  description: string;
  definition: string;
  parameters: ReportParameter[];
  columns: ReportColumn[];
  chart: ReportChartHint | null;
  containsPatientIdentifiers: boolean;
  canSchedule: boolean;
  maxDays: number;
}

export interface ReportCategoryInfo {
  id: ReportCategory;
  label: string;
  count: number;
}

export interface PeriodPreset {
  value: string;
  label: string;
}

export interface ReportCatalogue {
  categories: ReportCategoryInfo[];
  reports: ReportCatalogueItem[];
  periods: PeriodPreset[];
  canManageDashboards: boolean;
  canManageSchedules: boolean;
  canSeePatientLevel: boolean;
  canConsolidate: boolean;
  dataSource: string;
}

// ------------------------------------------------------------------------------------------------ results

export type CellValue = string | number | boolean | null;

export interface ReportFigure {
  key: string;
  label: string;
  value: CellValue;
  type: ReportValueType;
}

export interface ReportChartSeries {
  key: string;
  label: string;
  type: ReportValueType;
  values: (number | null)[];
}

export interface ReportChartData {
  type: ReportChartType;
  categoryKey: string;
  categoryLabel: string;
  categoryType: ReportValueType;
  categories: string[];
  series: ReportChartSeries[];
  timeSeries: boolean;
  keys: (string | null)[] | null;
}

export interface ReportDrillDown {
  reportId: string;
  parameterKey: string;
  label: string;
  parameters: Record<string, string> | null;
}

export interface ReportFilter {
  label: string;
  value: string;
}

export interface ReportResult {
  reportId: string;
  title: string;
  category: ReportCategory;
  from: string | null;
  to: string | null;
  facilityName: string;
  filters: ReportFilter[];
  parameters: Record<string, string>;
  columns: ReportColumn[];
  rows: CellValue[][];
  rowKeys: (string | null)[] | null;
  totals: CellValue[] | null;
  summary: ReportFigure[];
  chart: ReportChartData | null;
  drillDown: ReportDrillDown | null;
  notes: string[];
  page: number;
  pageSize: number;
  totalRows: number;
  truncated: boolean;
  sort: string | null;
  desc: boolean;
  generatedAt: string;
  dataSource: string;
  containsPatientIdentifiers: boolean;
  /** Set when the report is longer than a PDF holds: what the PDF leaves out (the Excel file has every row). */
  pdfNotice: string | null;
}

/** Report inputs by parameter key (period, from, to, facilityId, departmentId, groupBy…). */
export type ReportInputs = Record<string, string>;

export interface RunOptions {
  page?: number;
  pageSize?: number;
  sort?: string;
  desc?: boolean;
  chart?: ReportChartType;
}

export type ExportFormat = 'Xlsx' | 'Csv' | 'Pdf';

// ------------------------------------------------------------------------------------------------ KPIs

export type KpiUnit = 'count' | 'percent' | 'days' | 'minutes' | 'inr' | 'score';

export interface KpiPoint {
  date: string;
  value: number;
}

export interface KpiValue {
  key: string;
  label: string;
  unit: KpiUnit;
  value: number | null;
  previous: number | null;
  changePercent: number | null;
  higherIsBetter: boolean;
  reportId: string | null;
  definition: string;
  trend: KpiPoint[] | null;
}

export interface KpiSet {
  from: string;
  to: string;
  previousFrom: string;
  previousTo: string;
  facilityName: string;
  kpis: KpiValue[];
  generatedAt: string;
  dataSource: string;
}

/** What one KPI means (GET /kpi-definitions): the same definitions the dashboard's tooltips read. */
export interface KpiDefinition {
  key: string;
  label: string;
  unit: KpiUnit;
  higherIsBetter: boolean;
  formula: string;
  source: string;
  target: string | null;
  audience: 'Operations' | 'Finance';
  reportId: string;
  /** Null when the user may not open the report. */
  reportTitle: string | null;
}

export interface ReportDefinition {
  id: string;
  title: string;
  category: ReportCategory;
  categoryLabel: string;
  description: string;
  definition: string;
  containsPatientIdentifiers: boolean;
  canSchedule: boolean;
  /** KPIs on the executive dashboard that drill into this report. */
  kpiKeys: string[];
}

export interface KpiDefinitions {
  kpis: KpiDefinition[];
  reports: ReportDefinition[];
}

// ------------------------------------------------------------------------------------------------ dashboards

export type WidgetWidth = 1 | 2 | 4;

export interface DashboardListItem {
  id: string;
  name: string;
  description: string | null;
  isShared: boolean;
  isTemplate: boolean;
  isMine: boolean;
  canEdit: boolean;
  ownerName: string | null;
  widgetCount: number;
  updatedAt: string;
  version: number;
}

export interface DashboardWidget {
  id: string;
  reportId: string;
  title: string;
  view: ReportChartType;
  width: WidgetWidth;
  parameters: ReportInputs;
  canView: boolean;
  reportTitle: string | null;
}

export interface DashboardDetail extends Omit<DashboardListItem, 'widgetCount'> {
  widgets: DashboardWidget[];
}

export interface SaveDashboardWidget {
  id: string | null;
  reportId: string;
  title: string | null;
  view: ReportChartType;
  width: WidgetWidth;
  parameters: ReportInputs;
}

export interface SaveDashboardRequest {
  name: string;
  description: string | null;
  isShared: boolean;
  widgets: SaveDashboardWidget[];
  version: number | null;
}

// ------------------------------------------------------------------------------------------------ schedules

/** Hourly is for BI extracts only. */
export type ScheduleFrequency = 'Hourly' | 'Daily' | 'Weekly' | 'Monthly';
/** Where a schedule's file goes: e-mailed, or written to the BI extract folder for Power BI and similar tools. */
export type ScheduleDestination = 'Email' | 'ExtractFolder';
/** How fresh a BI extract's data is. */
export type ExtractState = 'Waiting' | 'Fresh' | 'Late' | 'Failing' | 'Paused';
export type Weekday = 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday' | 'Sunday';
export type ScheduleRunStatus = 'Succeeded' | 'Failed' | 'Skipped';
export type DeliveryStatus = 'Generating' | 'Ready' | 'Failed' | 'Skipped';
export type DeliveryTrigger = 'Scheduled' | 'Manual';
export type RecipientStatus = 'Pending' | 'Sent' | 'Cancelled';

export interface ScheduleDto {
  id: string;
  name: string;
  reportId: string;
  reportTitle: string;
  parameters: ReportInputs;
  format: ExportFormat;
  frequency: ScheduleFrequency;
  time: string;
  dayOfWeek: Weekday | null;
  dayOfMonth: number | null;
  when: string;
  recipients: string[];
  isActive: boolean;
  ownerName: string;
  isMine: boolean;
  nextRunAt: string | null;
  lastRunAt: string | null;
  lastStatus: ScheduleRunStatus | null;
  lastError: string | null;
  version: number;
  destination: ScheduleDestination;
  /** The extract's file name without ".csv"; null for e-mails. */
  extractName: string | null;
  /** The file the extract writes in the facility's folder. */
  extractFile: string | null;
  /** When the extract's file was last written: how fresh the data is that the BI tool reads. */
  lastSucceededAt: string | null;
  /** Null for e-mails. */
  extractState: ExtractState | null;
}

export interface SaveScheduleRequest {
  name: string;
  reportId: string;
  parameters: ReportInputs;
  format: ExportFormat;
  frequency: ScheduleFrequency;
  time: string;
  dayOfWeek: Weekday | null;
  dayOfMonth: number | null;
  recipients: string[];
  isActive: boolean;
  version: number | null;
  destination: ScheduleDestination;
  extractName: string | null;
}

/** Where BI extracts are written: the folder IT shares with the BI tool. */
export interface ExtractFolder {
  root: string;
  /** The signed-in user's facility's sub-folder, which holds its extracts. */
  facilityFolder: string | null;
  /** The file listing when each extract was last refreshed. */
  refreshLog: string;
  /** Null when HC360 can write the folder; otherwise what is wrong. */
  problem: string | null;
}

export interface DeliveryRecipient {
  email: string;
  status: RecipientStatus;
  attempts: number;
  lastError: string | null;
  sentAt: string | null;
}

export interface DeliveryDto {
  id: string;
  scheduleId: string;
  reportTitle: string;
  trigger: DeliveryTrigger;
  createdAt: string;
  status: DeliveryStatus;
  periodFrom: string | null;
  periodTo: string | null;
  format: ExportFormat;
  fileName: string | null;
  sizeBytes: number;
  rowCount: number;
  fileAvailable: boolean;
  error: string | null;
  sent: number;
  pending: number;
  recipients: DeliveryRecipient[];
}

export interface AnalyticsStatus {
  dataSource: string;
  usesReplica: boolean;
  replicaLagSeconds: number | null;
  /** E-mail schedules only, like the two figures after it. */
  activeSchedules: number;
  lastDeliveryAt: string | null;
  failedDeliveriesLast7Days: number;
  checkedAt: string;
  activeExtracts: number;
  lateExtracts: number;
  failingExtracts: number;
  lastExtractRefreshAt: string | null;
}

export type LookupKind = 'facilities' | 'departments' | 'practitioners' | 'payers' | 'wards' | 'stores' | 'theatres';

// ------------------------------------------------------------------------------------------------ queries

export const analyticsKeys = {
  all: ['analytics'] as const,
  catalogue: ['analytics', 'catalogue'] as const,
  runs: ['analytics', 'run'] as const,
  kpis: ['analytics', 'kpis'] as const,
  definitions: ['analytics', 'kpi-definitions'] as const,
  dashboards: ['analytics', 'dashboards'] as const,
  schedules: ['analytics', 'schedules'] as const,
};

/** Report inputs plus paging/sorting as GET query parameters (the GET run is not stored by the idempotency layer). */
function runQuery(inputs: ReportInputs, options: RunOptions): Record<string, string | number | boolean | undefined> {
  return {
    ...inputs,
    page: options.page,
    pageSize: options.pageSize,
    sort: options.sort,
    desc: options.desc,
    chart: options.chart,
  };
}

export const analyticsQueries = {
  catalogue: () =>
    queryOptions({
      queryKey: analyticsKeys.catalogue,
      queryFn: ({ signal }) => api.get<ReportCatalogue>(`${BASE}/reports`, { signal }),
      staleTime: 5 * 60_000,
    }),
  run: (reportId: string, inputs: ReportInputs, options: RunOptions = {}) =>
    queryOptions({
      queryKey: [...analyticsKeys.runs, reportId, inputs, options],
      queryFn: ({ signal }) =>
        api.get<ReportResult>(`${BASE}/reports/${encodeURIComponent(reportId)}/run`, {
          query: runQuery(inputs, options),
          signal,
          timeoutMs: 120_000,
        }),
      // Keep the previous page on screen while new inputs load, but never another report's columns.
      placeholderData: (previous) => (previous?.reportId === reportId ? previous : undefined),
      staleTime: 60_000,
    }),
  definitions: () =>
    queryOptions({
      queryKey: analyticsKeys.definitions,
      queryFn: ({ signal }) => api.get<KpiDefinitions>(`${BASE}/kpi-definitions`, { signal }),
      // Definitions change only with a new HC360 release.
      staleTime: 30 * 60_000,
    }),
  kpis: (from: string | undefined, to: string | undefined, facilityId: string | undefined) =>
    queryOptions({
      queryKey: [...analyticsKeys.kpis, from ?? '', to ?? '', facilityId ?? ''],
      queryFn: ({ signal }) =>
        api.get<KpiSet>(`${BASE}/kpis`, { query: { from, to, facilityId }, signal, timeoutMs: 120_000 }),
      placeholderData: keepPreviousData,
      staleTime: 2 * 60_000,
      refetchInterval: 5 * 60_000,
    }),
  /** Choices for report inputs (at most 200; `search` narrows long lists such as doctors). */
  lookup: (kind: LookupKind, facilityId?: string | null, search?: string) =>
    queryOptions({
      queryKey: ['analytics', 'lookup', kind, facilityId ?? '', search ?? ''],
      queryFn: ({ signal }) =>
        api.get<LookupItem[]>(`${BASE}/lookups/${kind}`, {
          query: { facilityId: facilityId ?? undefined, search: search || undefined },
          signal,
        }),
      staleTime: 10 * 60_000,
      placeholderData: keepPreviousData,
    }),
  status: () =>
    queryOptions({
      queryKey: ['analytics', 'status'],
      queryFn: ({ signal }) => api.get<AnalyticsStatus>(`${BASE}/status`, { signal }),
      staleTime: 60_000,
    }),
  dashboards: () =>
    queryOptions({
      queryKey: analyticsKeys.dashboards,
      queryFn: ({ signal }) => api.get<DashboardListItem[]>(`${BASE}/dashboards`, { signal }),
    }),
  dashboard: (id: string) =>
    queryOptions({
      queryKey: [...analyticsKeys.dashboards, id],
      queryFn: ({ signal }) => api.get<DashboardDetail>(`${BASE}/dashboards/${id}`, { signal }),
    }),
  /** The facility's e-mails or extracts (the server lists at most 500 of the kind asked for). */
  schedules: (destination: ScheduleDestination) =>
    queryOptions({
      queryKey: [...analyticsKeys.schedules, 'list', destination],
      queryFn: ({ signal }) => api.get<ScheduleDto[]>(`${BASE}/schedules`, { query: { destination }, signal }),
    }),
  extractFolder: () =>
    queryOptions({
      queryKey: [...analyticsKeys.schedules, 'extract-folder'],
      queryFn: ({ signal }) => api.get<ExtractFolder>(`${BASE}/extracts/folder`, { signal }),
      staleTime: 60_000,
    }),
  deliveries: (scheduleId: string, page: number) =>
    queryOptions({
      queryKey: [...analyticsKeys.schedules, scheduleId, 'deliveries', page],
      queryFn: ({ signal }) =>
        api.get<PagedResult<DeliveryDto>>(`${BASE}/schedules/${scheduleId}/deliveries`, {
          query: { page, pageSize: 10 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
};

// ------------------------------------------------------------------------------------------------ commands

export const analyticsApi = {
  /**
   * The report as an Excel, CSV or print-ready PDF file (header, inputs, generated by, "Confidential"). A PDF that
   * leaves rows out says so in the `X-Report-Notice` header.
   */
  exportReport: (reportId: string, inputs: ReportInputs, format: ExportFormat, sort?: string, desc?: boolean) =>
    api.download(`${BASE}/reports/${encodeURIComponent(reportId)}/export`, {
      query: { ...inputs, format: format.toLowerCase(), sort, desc },
      timeoutMs: 180_000,
    }),
  createDashboard: (body: SaveDashboardRequest) => api.post<DashboardDetail>(`${BASE}/dashboards`, body),
  updateDashboard: (id: string, body: SaveDashboardRequest) =>
    api.put<DashboardDetail>(`${BASE}/dashboards/${id}`, body),
  copyDashboard: (id: string) => api.post<DashboardDetail>(`${BASE}/dashboards/${id}/copy`),
  deleteDashboard: (id: string, version: number) =>
    api.delete<void>(`${BASE}/dashboards/${id}`, { query: { version } }),
  createSchedule: (body: SaveScheduleRequest) => api.post<ScheduleDto>(`${BASE}/schedules`, body),
  updateSchedule: (id: string, body: SaveScheduleRequest) => api.put<ScheduleDto>(`${BASE}/schedules/${id}`, body),
  deleteSchedule: (id: string, version: number) => api.delete<void>(`${BASE}/schedules/${id}`, { query: { version } }),
  /** Generates the file now: e-mails it, or rewrites the extract's file. The regular schedule does not change. */
  sendNow: (id: string) => api.post<DeliveryDto>(`${BASE}/schedules/${id}/send-now`, undefined, { timeoutMs: 180_000 }),
  deliveryFile: (deliveryId: string) => api.blob(`${BASE}/deliveries/${deliveryId}/file`, { timeoutMs: 120_000 }),
};
