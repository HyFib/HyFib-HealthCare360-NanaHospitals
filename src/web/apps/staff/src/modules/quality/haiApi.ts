import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Hospital-acquired infection surveillance (NABH HIC). Mirrors `HaiEndpoints` (`/api/v1/quality/hai`): the device register
 * (`quality.hai.devices`, nurses and doctors), the case review against the NHSN definitions and the rates
 * (`quality.hai.manage`, the infection control team).
 */

export const HAI_MANAGE = 'quality.hai.manage';
export const HAI_DEVICES = 'quality.hai.devices';

export type InvasiveDevice = 'CentralLine' | 'UrinaryCatheter' | 'Ventilator';
export type DeviceRemovalReason = 'Planned' | 'Accidental' | 'Replaced' | 'PatientDied' | 'PatientLeft';
export type HaiType = 'Clabsi' | 'Cauti' | 'Vap' | 'Ssi';
export type HaiCaseStatus = 'Suspected' | 'Confirmed' | 'RuledOut';
export type HaiCaseOrigin = 'CultureAlert' | 'Manual';

export const DEVICES: readonly InvasiveDevice[] = ['CentralLine', 'UrinaryCatheter', 'Ventilator'];
export const REMOVAL_REASONS: readonly DeviceRemovalReason[] = [
  'Planned',
  'Accidental',
  'Replaced',
  'PatientDied',
  'PatientLeft',
];
export const HAI_TYPES: readonly HaiType[] = ['Clabsi', 'Cauti', 'Vap', 'Ssi'];

export const DEVICE_LABELS: Record<InvasiveDevice, string> = {
  CentralLine: 'Central line',
  UrinaryCatheter: 'Urinary catheter',
  Ventilator: 'Ventilator',
};

export const REMOVAL_LABELS: Record<DeviceRemovalReason, string> = {
  Planned: 'Taken out as planned (planned extubation)',
  Accidental: 'Came out by accident (unplanned extubation)',
  Replaced: 'Replaced by a new one',
  PatientDied: 'The patient died',
  PatientLeft: 'Discharged or transferred with it',
};

export const HAI_LABELS: Record<HaiType, string> = {
  Clabsi: 'CLABSI',
  Cauti: 'CAUTI',
  Vap: 'VAP',
  Ssi: 'Surgical site infection',
};

export const STATUS_LABELS: Record<HaiCaseStatus, string> = {
  Suspected: 'To review',
  Confirmed: 'Confirmed',
  RuledOut: 'Ruled out',
};

export interface DeviceEpisode {
  id: string;
  patientId: string;
  uhid: string | null;
  admissionId: string;
  admissionNumber: string;
  wardName: string | null;
  device: InvasiveDevice;
  detail: string | null;
  insertedAt: string;
  insertedByName: string | null;
  removedAt: string | null;
  removalReason: DeviceRemovalReason | null;
  removedByName: string | null;
  deviceDays: number;
  notes: string | null;
  version: number;
}

export interface HaiCaseListItem {
  id: string;
  caseNumber: string;
  type: HaiType;
  origin: HaiCaseOrigin;
  status: HaiCaseStatus;
  uhid: string | null;
  eventOn: string;
  organism: string | null;
  specimen: string | null;
  meetsDefinition: boolean;
  createdAt: string;
}

export interface HaiCriterion {
  key: string;
  group: string;
  text: string;
  met: boolean;
}

export interface HaiCaseDetail {
  id: string;
  caseNumber: string;
  type: HaiType;
  origin: HaiCaseOrigin;
  status: HaiCaseStatus;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  admissionId: string | null;
  admissionNumber: string | null;
  device: DeviceEpisode | null;
  patientDevices: DeviceEpisode[];
  cultureResultId: string | null;
  organism: string | null;
  specimen: string | null;
  specimenCollectedAt: string | null;
  surgeryBookingId: string | null;
  surgeryReference: string | null;
  operatedOn: string | null;
  daysAfterOperation: number | null;
  eventOn: string;
  deviceEligible: boolean | null;
  dayOfDevice: number | null;
  criteria: HaiCriterion[];
  meetsDefinition: boolean;
  missing: string[];
  reviewedByName: string | null;
  reviewedAt: string | null;
  decisionNote: string | null;
  notes: string | null;
  version: number;
}

export interface SurgeryOption {
  bookingId: string;
  reference: string;
  operatedOn: string;
  elective: boolean;
}

export interface HaiRateRow {
  month: string;
  label: string;
  patientDays: number;
  centralLineDays: number;
  urinaryCatheterDays: number;
  ventilatorDays: number;
  clabsi: number;
  cauti: number;
  vap: number;
  ssi: number;
  operations: number;
  clabsiRate: number | null;
  cautiRate: number | null;
  vapRate: number | null;
  ssiRate: number | null;
  centralLineUtilisation: number | null;
  urinaryCatheterUtilisation: number | null;
  ventilatorUtilisation: number | null;
  suspected: number;
}

export interface InsertDeviceRequest {
  admissionId: string | null;
  admissionNumber: string | null;
  device: InvasiveDevice;
  detail: string | null;
  insertedAt: string;
  notes: string | null;
}

export interface ReviewHaiCaseRequest {
  criteriaMet: string[];
  eventOn: string;
  deviceEpisodeId: string | null;
  surgeryBookingId: string | null;
  surgeryReference: string | null;
  operatedOn: string | null;
  notes: string | null;
  version: number;
}

export const haiKeys = { all: ['quality', 'hai'] as const };

export const haiQueries = {
  devices: (inPlace?: boolean) =>
    queryOptions({
      queryKey: ['quality', 'hai', 'devices', inPlace ?? 'all'],
      queryFn: ({ signal }) => api.get<DeviceEpisode[]>('/api/v1/quality/hai/devices', { query: { inPlace }, signal }),
    }),
  cases: (status?: HaiCaseStatus) =>
    queryOptions({
      queryKey: ['quality', 'hai', 'cases', status ?? 'all'],
      queryFn: ({ signal }) => api.get<HaiCaseListItem[]>('/api/v1/quality/hai/cases', { query: { status }, signal }),
    }),
  case: (id: string) =>
    queryOptions({
      queryKey: ['quality', 'hai', 'case', id],
      queryFn: ({ signal }) => api.get<HaiCaseDetail>(`/api/v1/quality/hai/cases/${id}`, { signal }),
    }),
  surgeries: (id: string) =>
    queryOptions({
      queryKey: ['quality', 'hai', 'case', id, 'surgeries'],
      queryFn: ({ signal }) => api.get<SurgeryOption[]>(`/api/v1/quality/hai/cases/${id}/surgeries`, { signal }),
    }),
  rates: () =>
    queryOptions({
      queryKey: ['quality', 'hai', 'rates'],
      queryFn: ({ signal }) => api.get<HaiRateRow[]>('/api/v1/quality/hai/rates', { signal }),
    }),
};

export const haiApi = {
  insertDevice: (request: InsertDeviceRequest) => api.post<DeviceEpisode>('/api/v1/quality/hai/devices', request),
  removeDevice: (id: string, removedAt: string, reason: DeviceRemovalReason, version: number) =>
    api.post<DeviceEpisode>(`/api/v1/quality/hai/devices/${id}/remove`, { removedAt, reason, version }),
  review: (id: string, request: ReviewHaiCaseRequest) =>
    api.put<HaiCaseDetail>(`/api/v1/quality/hai/cases/${id}`, request),
  decide: (id: string, decision: 'confirm' | 'rule-out' | 'reopen', note: string | null, version: number) =>
    api.post<HaiCaseDetail>(`/api/v1/quality/hai/cases/${id}/${decision}`, { note, version }),
};

/** An ISO instant as the value of a datetime-local input, in the browser's time zone. */
export function isoToLocal(date: Date): string {
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

/** A datetime-local value as an ISO instant; null when it is not a date. */
export function localToIso(value: string): string | null {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}
