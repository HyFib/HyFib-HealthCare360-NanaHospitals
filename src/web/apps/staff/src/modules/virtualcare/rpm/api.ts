import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

export const READING_TYPES = ['SystolicBp', 'DiastolicBp', 'HeartRate', 'SpO2', 'Glucose', 'Weight', 'Temperature', 'EcgRhythm'] as const;
export type RpmReadingType = (typeof READING_TYPES)[number];

export const DEVICE_TYPES = ['BpMonitor', 'Glucometer', 'PulseOximeter', 'WeightScale', 'Thermometer', 'EcgPatch'] as const;
export type RpmDeviceType = (typeof DEVICE_TYPES)[number];

export const PROGRAM_KINDS = ['Hypertension', 'Diabetes', 'Cardiac', 'PostDischarge', 'Custom'] as const;
export type RpmProgramKind = (typeof PROGRAM_KINDS)[number];

export const ENROLLMENT_STATUSES = ['Active', 'Paused', 'Completed', 'Withdrawn'] as const;
export type RpmEnrollmentStatus = (typeof ENROLLMENT_STATUSES)[number];

export type ReadingSeverity = 'Normal' | 'Warning' | 'Critical';
export type ReadingSource = 'Device' | 'Fhir' | 'Manual';
export type RpmAlertKind = 'Threshold' | 'DeviceSilence';

export const ALERT_SEVERITIES = ['Warning', 'Critical'] as const;
export type RpmAlertSeverity = (typeof ALERT_SEVERITIES)[number];

export const ALERT_STATUSES = ['Open', 'Acknowledged', 'Resolved'] as const;
export type RpmAlertStatus = (typeof ALERT_STATUSES)[number];

export interface RpmProgramDto {
  id: string;
  code: string;
  name: string;
  kind: RpmProgramKind;
  description: string | null;
  expectedReadingsPerDay: number;
  isActive: boolean;
  version: number;
}

export interface SaveRpmProgramRequest {
  code: string;
  name: string;
  kind: RpmProgramKind;
  description: string | null;
  expectedReadingsPerDay: number;
  isActive: boolean;
  version: number | null;
}

export interface ThresholdDto {
  type: RpmReadingType;
  label: string;
  unit: string;
  min: number | null;
  max: number | null;
  criticalMin: number | null;
  criticalMax: number | null;
}

export interface CareTeamMember {
  practitionerId: string;
  name: string;
  isPrimary: boolean;
}

export interface RpmDeviceDto {
  id: string;
  serialNumber: string;
  type: RpmDeviceType;
  manufacturer: string | null;
  model: string | null;
  enrollmentId: string | null;
  patientId: string | null;
  assignedAt: string | null;
  lastSeenAt: string | null;
  apiKeyIssuedAt: string | null;
  isActive: boolean;
  silenceAlerted: boolean;
  version: number;
}

export interface RpmEnrollmentListItem {
  id: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  programId: string;
  programName: string;
  programKind: RpmProgramKind;
  status: RpmEnrollmentStatus;
  startDate: string;
  endDate: string | null;
  lastReadingAt: string | null;
  openAlerts: number;
  criticalAlerts: number;
}

export interface RpmEnrollmentDetail {
  id: string;
  facilityId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  phoneMasked: string | null;
  programId: string;
  programName: string;
  programKind: RpmProgramKind;
  status: RpmEnrollmentStatus;
  statusReason: string | null;
  startDate: string;
  endDate: string | null;
  goals: string | null;
  notes: string | null;
  careTeam: CareTeamMember[];
  thresholds: ThresholdDto[];
  devices: RpmDeviceDto[];
  lastReadingAt: string | null;
  thresholdsUpdatedAt: string | null;
  openAlerts: number;
  version: number;
}

export interface EnrollPatientRequest {
  patientId: string;
  programId: string;
  careTeamPractitionerIds: string[];
  primaryPractitionerId: string | null;
  startDate: string | null;
  goals: string | null;
  notes: string | null;
}

export interface UpdateEnrollmentRequest {
  careTeamPractitionerIds: string[];
  primaryPractitionerId: string | null;
  status: RpmEnrollmentStatus;
  statusReason: string | null;
  goals: string | null;
  notes: string | null;
  version: number;
}

export interface ThresholdInput {
  type: RpmReadingType;
  min: number | null;
  max: number | null;
  criticalMin: number | null;
  criticalMax: number | null;
}

export interface RpmReadingDto {
  id: string;
  type: RpmReadingType;
  value: number;
  unit: string;
  textValue: string | null;
  measuredAt: string;
  receivedAt: string;
  severity: ReadingSeverity;
  source: ReadingSource;
  deviceId: string | null;
}

export interface ManualReading {
  type: RpmReadingType;
  value: number;
  unit: string | null;
  measuredAt: string | null;
  textValue: string | null;
}

export interface IngestResult {
  accepted: number;
  duplicates: number;
  rejected: string[];
  alertsRaised: number;
}

export interface TrendPoint {
  measuredAt: string;
  value: number;
  severity: ReadingSeverity;
}

export interface TrendDay {
  date: string;
  min: number;
  max: number;
  average: number;
  count: number;
}

export interface RpmTrend {
  type: RpmReadingType;
  label: string;
  unit: string;
  threshold: ThresholdDto | null;
  points: TrendPoint[];
  daily: TrendDay[];
}

export interface RpmAdherenceDto {
  windowDays: number;
  daysWithReadings: number;
  percent: number;
  missedDates: string[];
  expectedReadingsPerDay: number;
  readingsPerDay: { date: string; readings: number }[];
}

export interface RegisterDeviceRequest {
  serialNumber: string;
  type: RpmDeviceType;
  manufacturer: string | null;
  model: string | null;
  enrollmentId: string | null;
}

/** The device API key is returned exactly once (only its hash is stored). */
export interface DeviceKeyIssued {
  deviceId: string;
  serialNumber: string;
  apiKey: string;
  ingestPath: string;
  headerName: string;
}

export interface RpmAlertDto {
  id: string;
  enrollmentId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  kind: RpmAlertKind;
  severity: RpmAlertSeverity;
  status: RpmAlertStatus;
  readingType: RpmReadingType | null;
  value: number | null;
  unit: string | null;
  message: string;
  raisedAt: string;
  lastOccurredAt: string;
  occurrences: number;
  acknowledgedAt: string | null;
  resolvedAt: string | null;
  notes: string | null;
}

export interface RpmDashboard {
  activeEnrollments: number;
  pausedEnrollments: number;
  openAlerts: number;
  criticalAlerts: number;
  unacknowledgedCritical: number;
  silentDevices: number;
  readingsLast24Hours: number;
  enrollmentsWithoutReadingToday: number;
}

// ------------------------------------------------------------------ queries

export interface EnrollmentFilters {
  status?: RpmEnrollmentStatus;
  programId?: string;
  practitionerId?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface AlertFilters {
  status?: RpmAlertStatus;
  severity?: RpmAlertSeverity;
  enrollmentId?: string;
  patientId?: string;
  page?: number;
  pageSize?: number;
}

/** Every RPM key starts with 'rpm' so one invalidation refreshes the module after a change. */
export const rpmKeys = {
  all: ['rpm'] as const,
  alerts: ['rpm', 'alerts'] as const,
  enrollment: (id: string) => ['rpm', 'enrollment', id] as const,
  devices: ['rpm', 'devices'] as const,
  programs: ['rpm', 'programs'] as const,
};

const BASE = '/api/v1/rpm';

/** No push channel exists for RPM; queues poll every minute (and on focus) so new alerts surface quickly. */
const POLL_MS = 60_000;

export const rpmQueries = {
  dashboard: () =>
    queryOptions({
      queryKey: ['rpm', 'dashboard'],
      queryFn: ({ signal }) => api.get<RpmDashboard>(`${BASE}/dashboard`, { signal }),
      refetchInterval: POLL_MS,
    }),
  programs: (activeOnly?: boolean) =>
    queryOptions({
      queryKey: [...rpmKeys.programs, activeOnly ?? false],
      queryFn: ({ signal }) => api.get<RpmProgramDto[]>(`${BASE}/programs`, { query: { activeOnly }, signal }),
      staleTime: 5 * 60_000,
    }),
  enrollments: (filters: EnrollmentFilters) =>
    queryOptions({
      queryKey: ['rpm', 'enrollments', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<RpmEnrollmentListItem>>(`${BASE}/enrollments`, { query: { ...filters, pageSize: filters.pageSize ?? 25 }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: POLL_MS,
    }),
  enrollment: (id: string) =>
    queryOptions({
      queryKey: rpmKeys.enrollment(id),
      queryFn: ({ signal }) => api.get<RpmEnrollmentDetail>(`${BASE}/enrollments/${id}`, { signal }),
    }),
  readings: (id: string, params: { type?: RpmReadingType; from?: string; to?: string; page?: number }) =>
    queryOptions({
      queryKey: ['rpm', 'readings', id, params],
      queryFn: ({ signal }) => api.get<PagedResult<RpmReadingDto>>(`${BASE}/enrollments/${id}/readings`, { query: { ...params, pageSize: 20 }, signal }),
      placeholderData: keepPreviousData,
    }),
  trends: (id: string, params: { from: string; to: string; types?: RpmReadingType[] }) =>
    queryOptions({
      queryKey: ['rpm', 'trends', id, params],
      queryFn: ({ signal }) =>
        api.get<RpmTrend[]>(`${BASE}/enrollments/${id}/trends`, { query: { from: params.from, to: params.to, types: params.types?.join(',') }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: POLL_MS,
    }),
  adherence: (id: string, days = 30) =>
    queryOptions({
      queryKey: ['rpm', 'adherence', id, days],
      queryFn: ({ signal }) => api.get<RpmAdherenceDto>(`${BASE}/enrollments/${id}/adherence`, { query: { days }, signal }),
    }),
  devices: (params: { enrollmentId?: string; activeOnly?: boolean; unassigned?: boolean }) =>
    queryOptions({
      queryKey: [...rpmKeys.devices, params],
      queryFn: ({ signal }) => api.get<RpmDeviceDto[]>(`${BASE}/devices`, { query: { ...params }, signal }),
    }),
  alerts: (filters: AlertFilters) =>
    queryOptions({
      queryKey: [...rpmKeys.alerts, filters],
      queryFn: ({ signal }) => api.get<PagedResult<RpmAlertDto>>(`${BASE}/alerts`, { query: { ...filters, pageSize: filters.pageSize ?? 25 }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: POLL_MS,
    }),
};

// ------------------------------------------------------------------ commands

export const rpmApi = {
  saveProgram: (id: string | null, request: SaveRpmProgramRequest) =>
    id ? api.put<void>(`${BASE}/programs/${id}`, request) : api.post<{ id: string }>(`${BASE}/programs`, request),
  enroll: (request: EnrollPatientRequest) => api.post<{ id: string }>(`${BASE}/enrollments`, request),
  updateEnrollment: (id: string, request: UpdateEnrollmentRequest) => api.put<void>(`${BASE}/enrollments/${id}`, request),
  deleteEnrollment: (id: string) => api.delete<void>(`${BASE}/enrollments/${id}`),
  setThresholds: (id: string, thresholds: ThresholdInput[], version: number) => api.put<void>(`${BASE}/enrollments/${id}/thresholds`, { thresholds, version }),
  recordReadings: (id: string, readings: ManualReading[]) => api.post<IngestResult>(`${BASE}/enrollments/${id}/readings`, { readings }),
  registerDevice: (request: RegisterDeviceRequest) => api.post<DeviceKeyIssued>(`${BASE}/devices`, request),
  rotateKey: (id: string) => api.post<DeviceKeyIssued>(`${BASE}/devices/${id}/rotate-key`),
  assignDevice: (id: string, enrollmentId: string | null) => api.post<void>(`${BASE}/devices/${id}/assign`, { enrollmentId }),
  deactivateDevice: (id: string) => api.post<void>(`${BASE}/devices/${id}/deactivate`),
  acknowledgeAlert: (id: string, notes: string | null) => api.post<void>(`${BASE}/alerts/${id}/acknowledge`, { notes }),
  resolveAlert: (id: string, notes: string | null) => api.post<void>(`${BASE}/alerts/${id}/resolve`, { notes }),
};
