import { isApiError, type Gender, type PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ChargeOutcome, ChargeStatus, Weekday } from '@/modules/ot/shared/types';

// ---------------------------------------------------------------- enums (JSON strings)

export const SERO_STATUSES = ['Negative', 'HBsAg', 'HCV', 'HIV'] as const;
export type SeroStatus = (typeof SERO_STATUSES)[number];

export const VASCULAR_ACCESS_TYPES = ['AVF', 'AVG', 'Catheter'] as const;
export type VascularAccessType = (typeof VASCULAR_ACCESS_TYPES)[number];

export const DIALYSIS_SHIFTS = ['Morning', 'Afternoon', 'Evening', 'Night'] as const;
export type DialysisShift = (typeof DIALYSIS_SHIFTS)[number];

export const MACHINE_STATUSES = ['Available', 'InUse', 'Maintenance', 'Retired'] as const;
export type DialysisMachineStatus = (typeof MACHINE_STATUSES)[number];

export const SESSION_STATUSES = ['Scheduled', 'InProgress', 'Completed', 'Cancelled', 'Missed'] as const;
export type DialysisSessionStatus = (typeof SESSION_STATUSES)[number];

export const REUSE_POLICIES = ['SingleUse', 'Reuse'] as const;
export type DialyzerReusePolicy = (typeof REUSE_POLICIES)[number];

export const COMPLICATIONS = [
  'Hypotension',
  'MuscleCramps',
  'NauseaVomiting',
  'Headache',
  'ChestPain',
  'FeverRigors',
  'Arrhythmia',
  'AccessBleeding',
  'CircuitClotting',
  'Hypoglycaemia',
  'Other',
] as const;
export type DialysisComplication = (typeof COMPLICATIONS)[number];

// ---------------------------------------------------------------- DTOs (mirror Features/Dialysis/DialysisContracts.cs)

export interface DialysisMachineDto {
  id: string;
  code: string;
  model: string;
  manufacturer: string | null;
  serialNumber: string | null;
  station: string | null;
  seroDedication: SeroStatus;
  status: DialysisMachineStatus;
  lastDisinfectedAt: string | null;
  lastDisinfectionMethod: string | null;
  lastUsedAt: string | null;
  needsDisinfection: boolean;
  isActive: boolean;
  version: number;
}

export interface SaveDialysisMachineRequest {
  code: string;
  model: string;
  manufacturer: string | null;
  serialNumber: string | null;
  station: string | null;
  seroDedication: SeroStatus;
  status: DialysisMachineStatus;
  isActive: boolean;
  version: number | null;
}

export interface DialysisProfileDto {
  id: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  dryWeightKg: number;
  vascularAccess: VascularAccessType;
  accessSite: string | null;
  accessCreatedOn: string | null;
  scheduleDays: Weekday[];
  shift: DialysisShift;
  sessionMinutes: number;
  seroStatus: SeroStatus;
  seroTestedOn: string | null;
  seroRescreeningDue: boolean;
  reusePolicy: DialyzerReusePolicy;
  maxDialyzerUses: number;
  effectiveMaxDialyzerUses: number;
  nephrologistId: string | null;
  nephrologistName: string | null;
  primaryDiagnosis: string | null;
  anticoagulationNotes: string | null;
  enrolledOn: string;
  isActive: boolean;
  version: number;
}

export interface SaveDialysisProfileRequest {
  dryWeightKg: number;
  vascularAccess: VascularAccessType;
  accessSite: string | null;
  accessCreatedOn: string | null;
  scheduleDays: Weekday[];
  shift: DialysisShift;
  sessionMinutes: number;
  seroStatus: SeroStatus;
  seroTestedOn: string;
  reusePolicy: DialyzerReusePolicy;
  maxDialyzerUses: number;
  nephrologistId: string | null;
  primaryDiagnosis: string | null;
  anticoagulationNotes: string | null;
  isActive: boolean;
  version: number | null;
}

export interface DialyzerDto {
  id: string;
  patientId: string;
  serialNumber: string;
  brand: string | null;
  model: string | null;
  surfaceAreaM2: number | null;
  usesCount: number;
  maxUses: number;
  remainingUses: number;
  firstUsedAt: string | null;
  lastUsedAt: string | null;
  isDiscarded: boolean;
  discardedAt: string | null;
  discardReason: string | null;
}

export interface RegisterDialyzerRequest {
  serialNumber: string;
  brand: string | null;
  model: string | null;
  surfaceAreaM2: number | null;
}

export interface ScheduleDialysisRequest {
  patientId: string;
  scheduledDate: string;
  shift: DialysisShift;
  machineId: string | null;
  visitId: string | null;
  admissionId: string | null;
}

export interface PreDialysisRequest {
  weightKg: number;
  systolic: number;
  diastolic: number;
  pulse: number;
  temperatureC: number | null;
  targetUfMl: number | null;
}

export interface StartDialysisRequest {
  machineId: string;
  dialyzerId: string | null;
  newDialyzer: RegisterDialyzerRequest | null;
  heparinLoadingUnits: number | null;
  heparinHourlyUnits: number | null;
}

export interface DialysisReading {
  at: string;
  systolic: number | null;
  diastolic: number | null;
  pulse: number | null;
  bloodFlowMlMin: number | null;
  ufRateMlHr: number | null;
  venousPressure: number | null;
  arterialPressure: number | null;
  transmembranePressure: number | null;
  notes: string | null;
}

export interface CompleteDialysisRequest {
  weightKg: number;
  systolic: number;
  diastolic: number;
  pulse: number | null;
  ufAchievedMl: number | null;
  complications: DialysisComplication[] | null;
  complicationNotes: string | null;
}

export interface DialysisSessionListItem {
  id: string;
  sessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  scheduledDate: string;
  shift: DialysisShift;
  status: DialysisSessionStatus;
  machineId: string | null;
  machineCode: string | null;
  startedAt: string | null;
  endedAt: string | null;
  chargeStatus: ChargeStatus;
  version: number;
}

export interface DialysisSessionDetail {
  id: string;
  sessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  seroStatus: SeroStatus | null;
  dryWeightKg: number | null;
  visitId: string | null;
  admissionId: string | null;
  scheduledDate: string;
  shift: DialysisShift;
  status: DialysisSessionStatus;
  machineId: string | null;
  machineCode: string | null;
  preWeightKg: number | null;
  preSystolic: number | null;
  preDiastolic: number | null;
  prePulse: number | null;
  preTemperatureC: number | null;
  targetUfMl: number | null;
  preAssessedAt: string | null;
  readings: DialysisReading[];
  postWeightKg: number | null;
  postSystolic: number | null;
  postDiastolic: number | null;
  postPulse: number | null;
  ufAchievedMl: number | null;
  heparinLoadingUnits: number | null;
  heparinHourlyUnits: number | null;
  dialyzerId: string | null;
  dialyzerSerial: string | null;
  dialyzerUseNumber: number | null;
  dialyzerMaxUses: number | null;
  complications: DialysisComplication[];
  complicationNotes: string | null;
  startedAt: string | null;
  endedAt: string | null;
  durationMinutes: number | null;
  performedByName: string | null;
  cancelReason: string | null;
  chargeStatus: ChargeStatus;
  chargeError: string | null;
  version: number;
}

export interface DialysisBoardMachine {
  id: string;
  code: string;
  station: string | null;
  seroDedication: SeroStatus;
  status: DialysisMachineStatus;
  needsDisinfection: boolean;
  sessions: DialysisSessionListItem[];
}

export interface DialysisDuePatient {
  patientId: string;
  uhid: string;
  patientName: string;
  seroStatus: SeroStatus;
  shift: DialysisShift;
  seroRescreeningDue: boolean;
}

export interface DialysisBoard {
  date: string;
  shift: DialysisShift | null;
  machines: DialysisBoardMachine[];
  unassigned: DialysisSessionListItem[];
  dueNotScheduled: DialysisDuePatient[];
}

// ---------------------------------------------------------------- queries

export interface SessionFilters {
  from?: string;
  to?: string;
  status?: DialysisSessionStatus;
  patientId?: string;
  search?: string;
  page?: number;
}

export interface ProfileFilters {
  sero?: SeroStatus;
  search?: string;
  includeInactive?: boolean;
  page?: number;
}

export const dialysisKeys = {
  all: ['dialysis'] as const,
  machines: (includeInactive: boolean) => ['dialysis', 'machines', includeInactive] as const,
  boards: ['dialysis', 'board'] as const,
  board: (date: string, shift?: DialysisShift) => ['dialysis', 'board', date, shift ?? 'all'] as const,
  sessionLists: ['dialysis', 'sessions'] as const,
  sessions: (filters: SessionFilters) => ['dialysis', 'sessions', filters] as const,
  session: (id: string) => ['dialysis', 'session', id] as const,
  profiles: (filters: ProfileFilters) => ['dialysis', 'profiles', filters] as const,
  profile: (patientId: string) => ['dialysis', 'profile', patientId] as const,
  dialyzers: (patientId: string, includeDiscarded: boolean) =>
    ['dialysis', 'dialyzers', patientId, includeDiscarded] as const,
};

export const dialysisQueries = {
  machines: (includeInactive = false) =>
    queryOptions({
      queryKey: dialysisKeys.machines(includeInactive),
      queryFn: ({ signal }) =>
        api.get<DialysisMachineDto[]>('/api/v1/dialysis/machines', { query: { includeInactive }, signal }),
      refetchInterval: 60_000,
    }),
  board: (date: string, shift?: DialysisShift) =>
    queryOptions({
      queryKey: dialysisKeys.board(date, shift),
      queryFn: ({ signal }) => api.get<DialysisBoard>('/api/v1/dialysis/board', { query: { date, shift }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  sessions: (filters: SessionFilters) =>
    queryOptions({
      queryKey: dialysisKeys.sessions(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<DialysisSessionListItem>>('/api/v1/dialysis/sessions', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  session: (id: string) =>
    queryOptions({
      queryKey: dialysisKeys.session(id),
      queryFn: ({ signal }) => api.get<DialysisSessionDetail>(`/api/v1/dialysis/sessions/${id}`, { signal }),
    }),
  profiles: (filters: ProfileFilters) =>
    queryOptions({
      queryKey: dialysisKeys.profiles(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<DialysisProfileDto>>('/api/v1/dialysis/patients', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  /** The patient's dialysis enrolment, or null when not enrolled (404). */
  profile: (patientId: string) =>
    queryOptions({
      queryKey: dialysisKeys.profile(patientId),
      queryFn: async ({ signal }) => {
        try {
          return await api.get<DialysisProfileDto>(`/api/v1/dialysis/patients/${patientId}`, { signal });
        } catch (error) {
          if (isApiError(error) && error.status === 404) {
            return null;
          }
          throw error;
        }
      },
    }),
  dialyzers: (patientId: string, includeDiscarded = false) =>
    queryOptions({
      queryKey: dialysisKeys.dialyzers(patientId, includeDiscarded),
      queryFn: ({ signal }) =>
        api.get<DialyzerDto[]>(`/api/v1/dialysis/patients/${patientId}/dialyzers`, {
          query: { includeDiscarded },
          signal,
        }),
    }),
};

export const dialysisApi = {
  saveMachine: async (id: string | null, request: SaveDialysisMachineRequest): Promise<void> => {
    if (id) {
      await api.put<void>(`/api/v1/dialysis/machines/${id}`, request);
    } else {
      await api.post<{ id: string }>('/api/v1/dialysis/machines', request);
    }
  },
  recordDisinfection: (machineId: string, method: string) =>
    api.post<void>(`/api/v1/dialysis/machines/${machineId}/disinfection`, { method }),
  saveProfile: (patientId: string, request: SaveDialysisProfileRequest) =>
    api.put<{ id: string }>(`/api/v1/dialysis/patients/${patientId}`, request),
  registerDialyzer: (patientId: string, request: RegisterDialyzerRequest) =>
    api.post<DialyzerDto>(`/api/v1/dialysis/patients/${patientId}/dialyzers`, request),
  discardDialyzer: (id: string, reason: string) =>
    api.post<void>(`/api/v1/dialysis/dialyzers/${id}/discard`, { reason }),
  schedule: (request: ScheduleDialysisRequest) => api.post<{ id: string }>('/api/v1/dialysis/sessions', request),
  /** `version`: the session as the nurse saw it; a session changed meanwhile is not overwritten (REL-04). */
  assignMachine: (sessionId: string, machineId: string, version: number) =>
    api.put<void>(`/api/v1/dialysis/sessions/${sessionId}/machine`, { machineId, version }),
  preAssessment: (sessionId: string, request: PreDialysisRequest) =>
    api.post<void>(`/api/v1/dialysis/sessions/${sessionId}/pre-assessment`, request),
  start: (sessionId: string, request: StartDialysisRequest) =>
    api.post<void>(`/api/v1/dialysis/sessions/${sessionId}/start`, request),
  addReadings: (sessionId: string, readings: DialysisReading[]) =>
    api.post<void>(`/api/v1/dialysis/sessions/${sessionId}/readings`, { readings }),
  complete: (sessionId: string, request: CompleteDialysisRequest) =>
    api.post<void>(`/api/v1/dialysis/sessions/${sessionId}/complete`, request),
  cancel: (sessionId: string, reason: string, patientDidNotCome: boolean) =>
    api.post<void>(`/api/v1/dialysis/sessions/${sessionId}/cancel`, { reason, patientDidNotCome }),
  retryCharges: (sessionId: string) => api.post<ChargeOutcome>(`/api/v1/dialysis/sessions/${sessionId}/charges/retry`),
};
