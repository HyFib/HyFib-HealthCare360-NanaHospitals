import type { Gender, PagedResult, PayerType, WristbandAllergies } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { BedAlert, BedStatus } from './bedState';

// ------------------------------------------------------------------ enums (strings, as the server sends them)

export const WARD_TYPES = [
  'General',
  'SemiPrivate',
  'Private',
  'Deluxe',
  'Icu',
  'Nicu',
  'Picu',
  'Hdu',
  'Maternity',
  'Isolation',
  'DayCare',
  'Emergency',
] as const;
export type WardType = (typeof WARD_TYPES)[number];

export const WARD_TYPE_LABEL: Record<WardType, string> = {
  General: 'General ward',
  SemiPrivate: 'Semi-private',
  Private: 'Private room',
  Deluxe: 'Deluxe room',
  Icu: 'ICU',
  Nicu: 'NICU (newborn)',
  Picu: 'PICU (children)',
  Hdu: 'HDU',
  Maternity: 'Maternity',
  Isolation: 'Isolation',
  DayCare: 'Day care',
  Emergency: 'Emergency',
};

export const GENDER_POLICIES = ['Any', 'MaleOnly', 'FemaleOnly'] as const;
export type WardGenderPolicy = (typeof GENDER_POLICIES)[number];
export const GENDER_POLICY_LABEL: Record<WardGenderPolicy, string> = {
  Any: 'Any patient',
  MaleOnly: 'Male patients only',
  FemaleOnly: 'Female patients only',
};

export const ADMISSION_TYPES = ['Elective', 'Emergency', 'DayCare', 'Maternity', 'TransferIn'] as const;
export type AdmissionType = (typeof ADMISSION_TYPES)[number];
export const ADMISSION_TYPE_LABEL: Record<AdmissionType, string> = {
  Elective: 'Planned (elective)',
  Emergency: 'Emergency',
  DayCare: 'Day care',
  Maternity: 'Maternity',
  TransferIn: 'Transfer from another hospital',
};

export const ADMISSION_SOURCES = ['OutPatient', 'Emergency', 'Referral', 'Direct'] as const;
export type AdmissionSource = (typeof ADMISSION_SOURCES)[number];
export const ADMISSION_SOURCE_LABEL: Record<AdmissionSource, string> = {
  OutPatient: 'Advised in OPD',
  Emergency: 'From casualty / ER',
  Referral: 'Referred by a doctor or hospital',
  Direct: 'Direct admission',
};

export const ADMISSION_STATUSES = [
  'Admitted',
  'DischargeInitiated',
  'Discharged',
  'Cancelled',
  'Absconded',
  'Expired',
  'Lama',
  'Dama',
] as const;
export type AdmissionStatus = (typeof ADMISSION_STATUSES)[number];
export const ADMISSION_STATUS_LABEL: Record<AdmissionStatus, string> = {
  Admitted: 'In hospital',
  DischargeInitiated: 'Discharge in progress',
  Discharged: 'Discharged',
  Cancelled: 'Cancelled',
  Absconded: 'Absconded',
  Expired: 'Expired',
  Lama: 'Left against advice (LAMA)',
  Dama: 'Discharged against advice (DAMA)',
};

export function isActiveAdmission(status: string): boolean {
  return status === 'Admitted' || status === 'DischargeInitiated';
}

export const DISCHARGE_TYPES = ['Normal', 'Referred', 'Lama', 'Dama', 'Absconded', 'Expired'] as const;
export type DischargeType = (typeof DISCHARGE_TYPES)[number];
export const DISCHARGE_TYPE_LABEL: Record<DischargeType, string> = {
  Normal: 'Normal discharge',
  Referred: 'Referred to another hospital',
  Lama: 'Left against medical advice (LAMA)',
  Dama: 'Discharged against medical advice (DAMA)',
  Absconded: 'Absconded',
  Expired: 'Death',
};

/** Routine discharges need initiation and billing clearance; death and abscondence are recorded at once. */
export function dischargeNeedsClearance(type: DischargeType): boolean {
  return type !== 'Expired' && type !== 'Absconded';
}

/** LAMA, DAMA and abscondence are medico-legal events: notes are required. */
export function dischargeNeedsNotes(type: DischargeType): boolean {
  return type === 'Lama' || type === 'Dama' || type === 'Absconded';
}

export const PAYER_TYPES: readonly PayerType[] = [
  'Self',
  'Insurance',
  'Corporate',
  'GovernmentScheme',
  'Staff',
  'Charity',
];
export const PAYER_LABEL: Record<PayerType, string> = {
  Self: 'Self (cash / card / UPI)',
  Insurance: 'Insurance / TPA',
  Corporate: 'Corporate',
  GovernmentScheme: 'Government scheme',
  Staff: 'Staff',
  Charity: 'Charity',
};

export type HousekeepingStatus = 'Pending' | 'InProgress' | 'Completed' | 'Cancelled';
export type HousekeepingTrigger = 'Discharge' | 'Transfer' | 'CancelledAdmission' | 'Maintenance' | 'Manual';
export const HOUSEKEEPING_TRIGGER_LABEL: Record<HousekeepingTrigger, string> = {
  Discharge: 'After discharge',
  Transfer: 'After transfer',
  CancelledAdmission: 'Cancelled admission',
  Maintenance: 'After maintenance',
  Manual: 'Requested',
};

export type BedAssignmentReason = 'Admission' | 'Transfer';
export type DailyChargeKind = 'Bed' | 'Nursing';

// ------------------------------------------------------------------ DTOs

export interface WardDto {
  id: string;
  code: string;
  name: string;
  type: WardType;
  floor: string | null;
  block: string | null;
  genderPolicy: WardGenderPolicy;
  nurseStation: string | null;
  phone: string | null;
  departmentId: string | null;
  bedServiceItemId: string | null;
  bedServiceCode: string | null;
  displayOrder: number;
  isActive: boolean;
  notes: string | null;
  totalBeds: number;
  occupiedBeds: number;
  version: number;
}

export interface SaveWardRequest {
  code: string;
  name: string;
  type: WardType;
  floor: string | null;
  block: string | null;
  genderPolicy: WardGenderPolicy;
  nurseStation: string | null;
  phone: string | null;
  departmentId: string | null;
  bedServiceItemId: string | null;
  displayOrder: number;
  isActive: boolean;
  notes: string | null;
  version: number | null;
}

export interface RoomDto {
  id: string;
  wardId: string;
  number: string;
  description: string | null;
  isIsolation: boolean;
  isActive: boolean;
  beds: number;
  version: number;
}

export interface SaveRoomRequest {
  wardId: string;
  number: string;
  description: string | null;
  isIsolation: boolean;
  isActive: boolean;
  version: number | null;
}

export interface BedDto {
  id: string;
  wardId: string;
  wardName: string | null;
  roomId: string | null;
  roomNumber: string | null;
  label: string;
  status: BedStatus;
  statusReason: string | null;
  statusChangedAt: string | null;
  currentAdmissionId: string | null;
  reservedForPatientId: string | null;
  reservedUntil: string | null;
  hasOxygen: boolean;
  hasVentilator: boolean;
  hasMonitor: boolean;
  displayOrder: number;
  isActive: boolean;
  nextStatuses: BedStatus[];
  version: number;
}

export interface SaveBedRequest {
  wardId: string;
  roomId: string | null;
  label: string;
  hasOxygen: boolean;
  hasVentilator: boolean;
  hasMonitor: boolean;
  displayOrder: number;
  isActive: boolean;
  version: number | null;
}

export interface CreateBedsRequest {
  wardId: string;
  roomId: string | null;
  labelPrefix: string;
  startNumber: number;
  count: number;
  hasOxygen: boolean;
  hasVentilator: boolean;
  hasMonitor: boolean;
}

export interface AvailableBedDto {
  bedId: string;
  label: string;
  wardId: string;
  wardName: string;
  wardType: WardType;
  roomNumber: string | null;
  status: BedStatus;
  hasOxygen: boolean;
  hasVentilator: boolean;
  hasMonitor: boolean;
  isEligible: boolean;
  ineligibleReason: string | null;
}

export interface BedOccupant {
  admissionId: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  consultantName: string | null;
  admittedAt: string;
  lengthOfStayDays: number;
  expectedDischargeDate: string | null;
  status: AdmissionStatus;
  payerType: PayerType;
}

export interface BedBoardItem {
  bedId: string;
  label: string;
  roomNumber: string | null;
  status: BedStatus;
  statusReason: string | null;
  statusChangedAt: string | null;
  hasOxygen: boolean;
  hasVentilator: boolean;
  hasMonitor: boolean;
  reservedUntil: string | null;
  occupant: BedOccupant | null;
  alerts: BedAlert[];
}

export interface BedCounts {
  total: number;
  occupied: number;
  vacant: number;
  reserved: number;
  cleaning: number;
  blocked: number;
  maintenance: number;
  operational: number;
  occupancyPercent: number;
}

export interface BedBoardWard {
  wardId: string;
  code: string;
  name: string;
  type: WardType;
  floor: string | null;
  nurseStation: string | null;
  counts: BedCounts;
  beds: BedBoardItem[];
}

export interface WardOccupancy {
  wardId: string;
  code: string;
  name: string;
  type: WardType;
  counts: BedCounts;
}

export interface OccupancyReport {
  asOf: string;
  total: BedCounts;
  wards: WardOccupancy[];
  byType: { type: WardType; counts: BedCounts }[];
}

export interface HousekeepingTaskDto {
  id: string;
  bedId: string;
  bedLabel: string;
  wardId: string;
  wardName: string;
  admissionId: string | null;
  trigger: HousekeepingTrigger;
  status: HousekeepingStatus;
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  turnaroundMinutes: number | null;
  waitingMinutes: number;
  notes: string | null;
}

export interface BedHistoryItem {
  admissionId: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  reason: BedAssignmentReason;
  fromAt: string;
  toAt: string | null;
}

export interface AdmitPatientRequest {
  patientId: string;
  bedId: string;
  type: AdmissionType;
  source: AdmissionSource;
  sourceVisitId: string | null;
  departmentId: string | null;
  admittingPractitionerId: string | null;
  consultantPractitionerId: string;
  provisionalDiagnosis: string | null;
  referredBy: string | null;
  payerType: PayerType;
  payerId: string | null;
  policyNumber: string | null;
  isMlc: boolean;
  mlcNumber: string | null;
  expectedDischargeDate: string | null;
  attendantName: string | null;
  attendantRelation: string | null;
  attendantPhone: string | null;
  admittedAt: string | null;
  notes: string | null;
  notifyPatient: boolean;
}

export interface AdmissionCreated {
  id: string;
  admissionNumber: string;
  visitId: string;
  visitNumber: string;
  bedId: string;
  bedLabel: string;
}

export interface UpdateAdmissionRequest {
  departmentId: string | null;
  consultantPractitionerId: string;
  provisionalDiagnosis: string | null;
  expectedDischargeDate: string | null;
  /** null keeps the stored policy number; an empty string removes it. */
  policyNumber: string | null;
  isMlc: boolean;
  mlcNumber: string | null;
  attendantName: string | null;
  attendantRelation: string | null;
  attendantPhone: string | null;
  notes: string | null;
  version: number;
}

export interface TransferPatientRequest {
  toBedId: string;
  reason: string;
  consultantPractitionerId: string | null;
  departmentId: string | null;
  transferredAt: string | null;
}

export interface AdmissionListItem {
  id: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  status: AdmissionStatus;
  type: AdmissionType;
  admittedAt: string;
  dischargedAt: string | null;
  wardId: string | null;
  wardName: string | null;
  bedId: string | null;
  bedLabel: string | null;
  consultantPractitionerId: string | null;
  consultantName: string | null;
  lengthOfStayDays: number;
  payerType: PayerType;
  isMlc: boolean;
}

/**
 * What an in-patient's wristband shows: who, the UHID to scan, the admission and the bed, and what the chart says about
 * allergies (the band's allergy mark). The allergies themselves are not included.
 */
export interface WristbandDto {
  admissionId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  admissionNumber: string;
  wardName: string | null;
  bedLabel: string | null;
  allergies: WristbandAllergies;
}

export interface AdmissionDetail {
  id: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  visitId: string;
  visitNumber: string | null;
  sourceVisitId: string | null;
  type: AdmissionType;
  source: AdmissionSource;
  status: AdmissionStatus;
  admittedAt: string;
  admittingPractitionerId: string | null;
  admittingPractitionerName: string | null;
  consultantPractitionerId: string | null;
  consultantName: string | null;
  departmentId: string | null;
  departmentName: string | null;
  wardId: string | null;
  wardName: string | null;
  bedId: string | null;
  bedLabel: string | null;
  provisionalDiagnosis: string | null;
  referredBy: string | null;
  payerType: PayerType;
  payerId: string | null;
  policyNumberMasked: string | null;
  isMlc: boolean;
  mlcNumber: string | null;
  expectedDischargeDate: string | null;
  attendantName: string | null;
  attendantRelation: string | null;
  attendantPhone: string | null;
  notes: string | null;
  lengthOfStayDays: number;
  dischargeInitiatedAt: string | null;
  dischargeType: DischargeType | null;
  dischargedAt: string | null;
  dischargeNotes: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  version: number;
}

export interface PatientAdmissionItem {
  id: string;
  admissionNumber: string;
  status: AdmissionStatus;
  type: AdmissionType;
  admittedAt: string;
  dischargedAt: string | null;
  wardId: string | null;
  wardName: string | null;
  consultantPractitionerId: string | null;
  consultantName: string | null;
  dischargeType: DischargeType | null;
  lengthOfStayDays: number;
}

export interface BedAssignmentDto {
  id: string;
  bedId: string;
  bedLabel: string;
  wardId: string;
  wardName: string;
  reason: BedAssignmentReason;
  transferReason: string | null;
  fromAt: string;
  toAt: string | null;
}

export interface ConsultantVisitDto {
  id: string;
  practitionerId: string;
  practitionerName: string | null;
  visitedAt: string;
  isCriticalCare: boolean;
  serviceCode: string;
  notes: string | null;
  isCharged: boolean;
  chargeError: string | null;
}

/** How worrying the latest vital signs are (the EMR's age-aware flags, NEWS2-aligned for adults). */
export type VitalsSeverity = 'Abnormal' | 'Critical';

/**
 * One in-patient on the ward round (GET /api/v1/inpatient/rounds). Visits, notes and results are the doctor's own on a
 * doctor's round, and anyone's on a department's.
 */
export interface WardRoundPatient {
  admissionId: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  wardId: string | null;
  wardName: string | null;
  bedLabel: string | null;
  isCriticalCare: boolean;
  status: AdmissionStatus;
  admittedAt: string;
  dayOfStay: number;
  expectedDischargeDate: string | null;
  provisionalDiagnosis: string | null;
  isMlc: boolean;
  /** A restricted (VIP) record the user may not open: the name reads "Restricted patient", no diagnosis or vital-sign details. */
  isRestricted: boolean;
  consultantId: string | null;
  consultantName: string | null;
  /** False when the doctor follows the patient for another consultant (visited during this stay). */
  isPrimaryConsultant: boolean;
  hasActiveAllergies: boolean;
  hasCriticalAlerts: boolean;
  lastVisitAt: string | null;
  seenToday: boolean;
  lastNoteAt: string | null;
  /** Results of this stay not yet reviewed by a doctor, whoever ordered them. */
  resultsToReview: number;
  hasCriticalResults: boolean;
  vitalsAt: string | null;
  vitalsSeverity: VitalsSeverity | null;
  /** Up to three plain-language flags, most serious first. */
  vitalsFlags: string[];
}

/** The doctor's (or the department's) in-patients, in the bed board's order. */
export interface WardRound {
  date: string;
  practitionerId: string | null;
  departmentId: string | null;
  patients: WardRoundPatient[];
}

export interface WardRoundParams {
  practitionerId?: string;
  departmentId?: string;
}

export interface DailyChargeDto {
  chargeDate: string;
  kind: DailyChargeKind;
  serviceItemId: string;
  serviceCode: string | null;
  sourceReference: string;
  chargeId: string;
  postedAt: string;
  cancelledAt: string | null;
}

export interface DischargeClearanceDto {
  admissionId: string;
  cleared: boolean;
  outstanding: number;
  reason: string | null;
}

export interface DischargeResult {
  admissionId: string;
  status: AdmissionStatus;
  dischargeInitiatedAt: string | null;
  dischargedAt: string | null;
  chargesPosted: number;
}

export interface PendingDischargeItem {
  admissionId: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  wardName: string | null;
  bedLabel: string | null;
  consultantName: string | null;
  dischargeInitiatedAt: string;
  expectedDischargeDate: string | null;
  waitingMinutes: number;
}

export interface DailyCensus {
  date: string;
  admissions: number;
  discharges: number;
  deaths: number;
  transfersIn: number;
  transfersOut: number;
  midnightCensus: number;
}

export interface CensusSummary {
  from: string;
  to: string;
  admissions: number;
  discharges: number;
  deaths: number;
  inpatientDays: number;
  averageLengthOfStay: number;
  bedOccupancyRate: number;
  days: DailyCensus[];
}

export interface WardCensus {
  wardId: string;
  code: string;
  name: string;
  type: WardType;
  bedsInService: number;
  summary: CensusSummary;
}

export interface CensusReport {
  from: string;
  to: string;
  bedsInService: number;
  hospital: CensusSummary;
  wards: WardCensus[];
  housekeepingTasksCompleted: number;
  averageHousekeepingTurnaroundMinutes: number | null;
}

/** Bed-charge / diet service from the masters look-up (used for ward tariffs). */
export interface ServiceItemOption {
  id: string;
  code: string;
  name: string;
  category: string;
  basePrice: number;
  isActive: boolean;
}

// ------------------------------------------------------------------ queries

export interface AdmissionFilters {
  search?: string;
  status?: AdmissionStatus;
  activeOnly?: boolean;
  wardId?: string;
  consultantId?: string;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export const inpatientKeys = {
  all: ['inpatient'] as const,
  wards: (includeInactive = false) => ['inpatient', 'wards', includeInactive] as const,
  rooms: (wardId: string) => ['inpatient', 'rooms', wardId] as const,
  beds: (wardId: string | null, includeInactive: boolean) =>
    ['inpatient', 'beds', wardId ?? 'all', includeInactive] as const,
  board: (wardId?: string | null) => ['inpatient', 'board', wardId ?? 'all'] as const,
  boardAll: ['inpatient', 'board'] as const,
  occupancy: ['inpatient', 'occupancy'] as const,
  available: (patientId: string | null, wardId: string | null) =>
    ['inpatient', 'available', patientId ?? 'any', wardId ?? 'all'] as const,
  housekeeping: (wardId?: string | null) => ['inpatient', 'housekeeping', wardId ?? 'all'] as const,
  bedHistory: (bedId: string) => ['inpatient', 'bed-history', bedId] as const,
  admissions: (filters: AdmissionFilters) => ['inpatient', 'admissions', filters] as const,
  admissionsAll: ['inpatient', 'admissions'] as const,
  admission: (id: string) => ['inpatient', 'admission', id] as const,
  pendingDischarges: (wardId?: string | null) => ['inpatient', 'pending-discharges', wardId ?? 'all'] as const,
  census: (from: string, to: string, wardId?: string | null) =>
    ['inpatient', 'census', from, to, wardId ?? 'all'] as const,
  patientAdmissions: (patientId: string) => ['inpatient', 'patient-admissions', patientId] as const,
  rounds: (params: WardRoundParams) => ['inpatient', 'rounds', params] as const,
  roundsAll: ['inpatient', 'rounds'] as const,
};

export const inpatientQueries = {
  wards: (includeInactive = false) =>
    queryOptions({
      queryKey: inpatientKeys.wards(includeInactive),
      queryFn: ({ signal }) =>
        api.get<WardDto[]>('/api/v1/inpatient/wards', {
          query: { includeInactive: includeInactive || undefined },
          signal,
        }),
      staleTime: 5 * 60_000,
    }),
  rooms: (wardId: string) =>
    queryOptions({
      queryKey: inpatientKeys.rooms(wardId),
      queryFn: ({ signal }) => api.get<RoomDto[]>(`/api/v1/inpatient/wards/${wardId}/rooms`, { signal }),
    }),
  beds: (wardId: string | null, includeInactive = true) =>
    queryOptions({
      queryKey: inpatientKeys.beds(wardId, includeInactive),
      queryFn: ({ signal }) =>
        api.get<BedDto[]>('/api/v1/beds', { query: { wardId, includeInactive: includeInactive || undefined }, signal }),
    }),
  board: (wardId?: string | null) =>
    queryOptions({
      queryKey: inpatientKeys.board(wardId),
      queryFn: ({ signal }) => api.get<BedBoardWard[]>('/api/v1/beds/board', { query: { wardId }, signal }),
      // Realtime pushes keep it fresh; the interval is the safety net after a missed event.
      refetchInterval: 60_000,
    }),
  occupancy: () =>
    queryOptions({
      queryKey: inpatientKeys.occupancy,
      queryFn: ({ signal }) => api.get<OccupancyReport>('/api/v1/beds/occupancy', { signal }),
      refetchInterval: 120_000,
    }),
  available: (patientId: string | null, wardId: string | null = null) =>
    queryOptions({
      queryKey: inpatientKeys.available(patientId, wardId),
      queryFn: ({ signal }) =>
        api.get<AvailableBedDto[]>('/api/v1/beds/available', { query: { patientId, wardId }, signal }),
      staleTime: 10_000,
    }),
  housekeeping: (wardId?: string | null) =>
    queryOptions({
      queryKey: inpatientKeys.housekeeping(wardId),
      queryFn: ({ signal }) =>
        api.get<HousekeepingTaskDto[]>('/api/v1/beds/housekeeping', { query: { wardId }, signal }),
      refetchInterval: 60_000,
    }),
  bedHistory: (bedId: string) =>
    queryOptions({
      queryKey: inpatientKeys.bedHistory(bedId),
      queryFn: ({ signal }) =>
        api.get<PagedResult<BedHistoryItem>>(`/api/v1/beds/${bedId}/history`, { query: { pageSize: 10 }, signal }),
    }),
  admissions: (filters: AdmissionFilters) =>
    queryOptions({
      queryKey: inpatientKeys.admissions(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<AdmissionListItem>>('/api/v1/inpatient/admissions', {
          query: { pageSize: 25, ...filters },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  admission: (id: string) =>
    queryOptions({
      queryKey: inpatientKeys.admission(id),
      queryFn: ({ signal }) => api.get<AdmissionDetail>(`/api/v1/inpatient/admissions/${id}`, { signal }),
    }),
  bedAssignments: (id: string) =>
    queryOptions({
      queryKey: [...inpatientKeys.admission(id), 'bed-history'],
      queryFn: ({ signal }) =>
        api.get<BedAssignmentDto[]>(`/api/v1/inpatient/admissions/${id}/bed-history`, { signal }),
    }),
  charges: (id: string) =>
    queryOptions({
      queryKey: [...inpatientKeys.admission(id), 'charges'],
      queryFn: ({ signal }) => api.get<DailyChargeDto[]>(`/api/v1/inpatient/admissions/${id}/charges`, { signal }),
    }),
  visits: (id: string) =>
    queryOptions({
      queryKey: [...inpatientKeys.admission(id), 'visits'],
      queryFn: ({ signal }) =>
        api.get<ConsultantVisitDto[]>(`/api/v1/inpatient/admissions/${id}/consultant-visits`, { signal }),
    }),
  clearance: (id: string) =>
    queryOptions({
      queryKey: [...inpatientKeys.admission(id), 'clearance'],
      queryFn: ({ signal }) =>
        api.get<DischargeClearanceDto>(`/api/v1/inpatient/admissions/${id}/discharge/clearance`, { signal }),
      staleTime: 0,
    }),
  /** The ward round; it follows visits recorded elsewhere within a minute. */
  rounds: (params: WardRoundParams) =>
    queryOptions({
      queryKey: inpatientKeys.rounds(params),
      queryFn: ({ signal }) => api.get<WardRound>('/api/v1/inpatient/rounds', { query: { ...params }, signal }),
      refetchInterval: 60_000,
    }),
  pendingDischarges: (wardId?: string | null) =>
    queryOptions({
      queryKey: inpatientKeys.pendingDischarges(wardId),
      queryFn: ({ signal }) =>
        api.get<PendingDischargeItem[]>('/api/v1/inpatient/discharges/pending', { query: { wardId }, signal }),
      refetchInterval: 120_000,
    }),
  census: (from: string, to: string, wardId?: string | null) =>
    queryOptions({
      queryKey: inpatientKeys.census(from, to, wardId),
      queryFn: ({ signal }) =>
        api.get<CensusReport>('/api/v1/inpatient/census', { query: { from, to, wardId }, signal }),
      placeholderData: keepPreviousData,
    }),
  patientAdmissions: (patientId: string) =>
    queryOptions({
      queryKey: inpatientKeys.patientAdmissions(patientId),
      queryFn: ({ signal }) => api.get<PatientAdmissionItem[]>(`/api/v1/patients/${patientId}/admissions`, { signal }),
      staleTime: 0,
    }),
  bedServices: () =>
    queryOptions({
      queryKey: ['inpatient', 'bed-services'],
      queryFn: ({ signal }) =>
        api.get<ServiceItemOption[]>('/api/v1/masters/lookups/services', { query: { category: 'BedCharge' }, signal }),
      staleTime: 10 * 60_000,
    }),
};

// ------------------------------------------------------------------ commands

export const bedApi = {
  reserve: (bedId: string, body: { patientId: string; reservedUntil: string; note: string | null }) =>
    api.post<BedDto>(`/api/v1/beds/${bedId}/reserve`, body),
  cancelReservation: (bedId: string) => api.post<BedDto>(`/api/v1/beds/${bedId}/cancel-reservation`),
  block: (bedId: string, reason: string) => api.post<BedDto>(`/api/v1/beds/${bedId}/block`, { reason }),
  unblock: (bedId: string) => api.post<BedDto>(`/api/v1/beds/${bedId}/unblock`),
  startMaintenance: (bedId: string, reason: string) =>
    api.post<BedDto>(`/api/v1/beds/${bedId}/maintenance`, { reason }),
  endMaintenance: (bedId: string) => api.post<BedDto>(`/api/v1/beds/${bedId}/maintenance/complete`),
  sendForCleaning: (bedId: string, notes: string | null) =>
    api.post<BedDto>(`/api/v1/beds/${bedId}/cleaning`, { notes }),
  startCleaning: (taskId: string) => api.post<HousekeepingTaskDto>(`/api/v1/beds/housekeeping/${taskId}/start`),
  completeCleaning: (taskId: string, notes: string | null) =>
    api.post<HousekeepingTaskDto>(`/api/v1/beds/housekeeping/${taskId}/complete`, { notes }),
  create: (body: SaveBedRequest) => api.post<BedDto>('/api/v1/beds', body),
  createMany: (body: CreateBedsRequest) => api.post<BedDto[]>('/api/v1/beds/bulk', body),
  update: (bedId: string, body: SaveBedRequest) => api.put<void>(`/api/v1/beds/${bedId}`, body),
};

export const wardApi = {
  create: (body: SaveWardRequest) => api.post<{ id: string }>('/api/v1/inpatient/wards', body),
  update: (wardId: string, body: SaveWardRequest) => api.put<void>(`/api/v1/inpatient/wards/${wardId}`, body),
  createRoom: (body: SaveRoomRequest) => api.post<{ id: string }>('/api/v1/inpatient/rooms', body),
  updateRoom: (roomId: string, body: SaveRoomRequest) => api.put<void>(`/api/v1/inpatient/rooms/${roomId}`, body),
};

export const admissionApi = {
  admit: (body: AdmitPatientRequest) => api.post<AdmissionCreated>('/api/v1/inpatient/admissions', body),
  update: (id: string, body: UpdateAdmissionRequest) => api.put<void>(`/api/v1/inpatient/admissions/${id}`, body),
  transfer: (id: string, body: TransferPatientRequest) =>
    api.post<void>(`/api/v1/inpatient/admissions/${id}/transfer`, body),
  cancel: (id: string, reason: string) => api.post<void>(`/api/v1/inpatient/admissions/${id}/cancel`, { reason }),
  initiateDischarge: (id: string, body: { expectedDischargeDate: string | null; notes: string | null }) =>
    api.post<DischargeResult>(`/api/v1/inpatient/admissions/${id}/discharge/initiate`, body),
  revokeDischarge: (id: string, reason: string) =>
    api.post<DischargeResult>(`/api/v1/inpatient/admissions/${id}/discharge/revoke`, { reason }),
  completeDischarge: (
    id: string,
    body: { dischargeType: DischargeType; dischargedAt: string | null; notes: string | null; notifyPatient: boolean },
  ) => api.post<DischargeResult>(`/api/v1/inpatient/admissions/${id}/discharge/complete`, body),
  recordVisit: (id: string, body: { practitionerId: string | null; visitedAt: string | null; notes: string | null }) =>
    api.post<ConsultantVisitDto>(`/api/v1/inpatient/admissions/${id}/consultant-visits`, body),
  cancelVisit: (visitId: string, reason: string) =>
    api.post<void>(`/api/v1/inpatient/admissions/consultant-visits/${visitId}/cancel`, { reason }),
  /** What the patient's wristband shows, read fresh for each print (the allergy mark comes from the chart). */
  wristband: (id: string) => api.get<WristbandDto>(`/api/v1/inpatient/admissions/${id}/wristband`),
};
