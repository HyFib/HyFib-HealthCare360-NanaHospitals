import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { FluidDirection, FluidRoute } from './fluidBalance';
import type { AmbulatoryAid, GaitType, PainScaleKind, RiskLevel } from './riskScales';
import type { NursingShift } from './time';

// ------------------------------------------------------------------ nursing notes and handovers

export const NOTE_CATEGORIES = ['General', 'Observation', 'Procedure', 'Incident', 'Education'] as const;
export type NursingNoteCategory = (typeof NOTE_CATEGORIES)[number];
export const NOTE_CATEGORY_LABEL: Record<NursingNoteCategory, string> = {
  General: 'General',
  Observation: 'Observation',
  Procedure: 'Procedure',
  Incident: 'Incident',
  Education: 'Patient education',
};

export interface NursingNoteDto {
  id: string;
  dutyDate: string;
  shift: NursingShift;
  category: NursingNoteCategory;
  note: string;
  noteFor: string;
  recordedAt: string;
  recordedByName: string | null;
  isLateEntry: boolean;
}

export interface HandoverDto {
  id: string;
  admissionId: string;
  patientId: string;
  wardId: string | null;
  dutyDate: string;
  fromShift: NursingShift;
  situation: string;
  background: string | null;
  assessment: string | null;
  recommendation: string | null;
  pendingTasks: string | null;
  handedOverAt: string;
  handedOverByName: string | null;
  acknowledgedAt: string | null;
  acknowledgedByName: string | null;
}

export interface WardHandoverItem {
  handover: HandoverDto;
  uhid: string;
  patientName: string;
  bedLabel: string | null;
}

export interface AddHandoverRequest {
  situation: string;
  background: string | null;
  assessment: string | null;
  recommendation: string | null;
  pendingTasks: string | null;
}

// ------------------------------------------------------------------ intake / output

export interface IntakeOutputDto {
  id: string;
  recordedAt: string;
  shift: NursingShift;
  direction: FluidDirection;
  route: FluidRoute;
  volumeMl: number;
  description: string | null;
  notes: string | null;
  recordedByName: string | null;
}

export interface FluidBalanceSummaryDto {
  from: string;
  to: string;
  total: { intakeMl: number; outputMl: number; balanceMl: number };
  byShift: { shift: NursingShift; intakeMl: number; outputMl: number; balanceMl: number }[];
  byRoute: { route: FluidRoute; direction: FluidDirection; volumeMl: number }[];
}

export interface IntakeOutputChart {
  dutyDate: string;
  balance: FluidBalanceSummaryDto;
  entries: IntakeOutputDto[];
}

export interface AddIntakeOutputRequest {
  direction: FluidDirection;
  route: FluidRoute;
  volumeMl: number;
  recordedAt: string | null;
  description: string | null;
  notes: string | null;
}

// ------------------------------------------------------------------ risk assessments

export type AssessmentType = 'MorseFall' | 'Braden' | 'Pain';
export const ASSESSMENT_LABEL: Record<AssessmentType, string> = { MorseFall: 'Fall risk (Morse)', Braden: 'Pressure injury (Braden)', Pain: 'Pain' };

export interface AssessmentDto {
  id: string;
  type: AssessmentType;
  score: number;
  risk: RiskLevel;
  interpretation: string;
  responses: Record<string, unknown> | null;
  assessedAt: string;
  assessedByName: string | null;
  notes: string | null;
}

export interface MorseAssessmentRequest {
  historyOfFalling: boolean;
  secondaryDiagnosis: boolean;
  ambulatoryAid: AmbulatoryAid;
  intravenousAccess: boolean;
  gait: GaitType;
  overestimatesAbility: boolean;
  notes: string | null;
}

export interface BradenAssessmentRequest {
  sensoryPerception: number;
  moisture: number;
  activity: number;
  mobility: number;
  nutrition: number;
  frictionShear: number;
  notes: string | null;
}

export interface PainAssessmentRequest {
  score: number;
  scale: PainScaleKind;
  location: string | null;
  character: string | null;
  notes: string | null;
}

// ------------------------------------------------------------------ medication chart and MAR

export type MedicationOrderSource = 'Prescription' | 'Pharmacy' | 'Manual';
export type MedicationOrderStatus = 'Active' | 'Stopped' | 'Completed';
export type MarStatus = 'Due' | 'Given' | 'Held' | 'Refused' | 'Missed' | 'Cancelled';
export type MarOutcome = 'Given' | 'Held' | 'Refused' | 'Missed';

export const MAR_STATUS_LABEL: Record<MarStatus, string> = {
  Due: 'Due',
  Given: 'Given',
  Held: 'Held',
  Refused: 'Refused',
  Missed: 'Missed',
  Cancelled: 'Cancelled',
};

export const ORDER_SOURCE_LABEL: Record<MedicationOrderSource, string> = {
  Prescription: 'E-prescription',
  Pharmacy: 'Issued by pharmacy',
  Manual: 'Transcribed on ward',
};

/** Frequencies the server schedules (OD, BD, TDS, QID, HS, 1-0-1, Q6H…) plus as-needed ones that never auto-schedule. */
export const COMMON_FREQUENCIES: { value: string; label: string }[] = [
  { value: 'OD', label: 'OD — once a day' },
  { value: 'BD', label: 'BD — twice a day' },
  { value: 'TDS', label: 'TDS — three times a day' },
  { value: 'QID', label: 'QID — four times a day' },
  { value: 'HS', label: 'HS — at bedtime' },
  { value: '1-0-1', label: '1-0-1 — morning and night' },
  { value: '1-1-1', label: '1-1-1 — morning, noon, night' },
  { value: 'Q4H', label: 'Q4H — every 4 hours' },
  { value: 'Q6H', label: 'Q6H — every 6 hours' },
  { value: 'Q8H', label: 'Q8H — every 8 hours' },
  { value: 'STAT', label: 'STAT — one dose now' },
  { value: 'SOS', label: 'SOS — only when needed' },
];

export const COMMON_ROUTES = ['Oral', 'IV', 'IM', 'SC', 'Sublingual', 'Topical', 'Inhalation', 'Nebulisation', 'Rectal', 'Nasogastric'];

export interface MedicationOrderDto {
  id: string;
  prescriptionId: string | null;
  drugItemId: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dose: string;
  route: string | null;
  frequency: string;
  durationDays: number | null;
  startAt: string;
  endAt: string | null;
  instructions: string | null;
  isControlledDrug: boolean;
  isAsNeeded: boolean;
  source: MedicationOrderSource;
  status: MedicationOrderStatus;
  lastDispensedAt: string | null;
  stoppedAt: string | null;
  stopReason: string | null;
  /** Medication round times, "HH:mm:ss" (IST). */
  roundTimes: string[];
}

export interface AddMedicationOrderRequest {
  drugItemId: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dose: string;
  route: string | null;
  frequency: string;
  durationDays: number | null;
  startAt: string | null;
  instructions: string | null;
  isControlledDrug: boolean;
  orderedByPractitionerId: string | null;
}

export interface MarEntryDto {
  id: string;
  medicationOrderId: string | null;
  drugName: string;
  dose: string;
  route: string | null;
  scheduledAt: string;
  status: MarStatus;
  isOverdue: boolean;
  isAdHoc: boolean;
  administeredAt: string | null;
  recordedByName: string | null;
  reason: string | null;
  batchNumber: string | null;
  witnessName: string | null;
  notes: string | null;
  version: number;
}

export interface AddAdHocDoseRequest {
  medicationOrderId: string | null;
  drugName: string | null;
  dose: string;
  route: string | null;
  scheduledAt: string | null;
  givenNow: boolean;
  reason: string | null;
  batchNumber: string | null;
  witnessName: string | null;
  notes: string | null;
}

export interface RecordAdministrationRequest {
  outcome: MarOutcome;
  administeredAt: string | null;
  reason: string | null;
  batchNumber: string | null;
  witnessName: string | null;
  notes: string | null;
  version: number;
}

export interface WardMarItem {
  entryId: string;
  admissionId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  bedLabel: string | null;
  drugName: string;
  dose: string;
  route: string | null;
  scheduledAt: string;
  isOverdue: boolean;
  minutesLate: number;
  version: number;
}

// ------------------------------------------------------------------ queries

export const nursingKeys = {
  all: ['nursing'] as const,
  admission: (admissionId: string) => ['nursing', 'admission', admissionId] as const,
  notes: (admissionId: string, dutyDate: string | null) => ['nursing', 'admission', admissionId, 'notes', dutyDate ?? 'all'] as const,
  handovers: (admissionId: string) => ['nursing', 'admission', admissionId, 'handovers'] as const,
  intakeOutput: (admissionId: string, dutyDate: string) => ['nursing', 'admission', admissionId, 'io', dutyDate] as const,
  assessments: (admissionId: string) => ['nursing', 'admission', admissionId, 'assessments'] as const,
  orders: (admissionId: string, activeOnly: boolean) => ['nursing', 'admission', admissionId, 'orders', activeOnly] as const,
  mar: (admissionId: string, date: string) => ['nursing', 'admission', admissionId, 'mar', date] as const,
  ward: (wardId: string) => ['nursing', 'ward', wardId] as const,
  wardHandovers: (wardId: string, dutyDate: string, shift: NursingShift | null) => ['nursing', 'ward', wardId, 'handovers', dutyDate, shift ?? 'all'] as const,
  wardMarDue: (wardId: string, withinMinutes: number) => ['nursing', 'ward', wardId, 'mar-due', withinMinutes] as const,
};

export const nursingQueries = {
  notes: (admissionId: string, dutyDate: string | null) =>
    queryOptions({
      queryKey: nursingKeys.notes(admissionId, dutyDate),
      queryFn: ({ signal }) => api.get<NursingNoteDto[]>(`/api/v1/nursing/admissions/${admissionId}/notes`, { query: { dutyDate }, signal }),
    }),
  handovers: (admissionId: string) =>
    queryOptions({
      queryKey: nursingKeys.handovers(admissionId),
      queryFn: ({ signal }) => api.get<HandoverDto[]>(`/api/v1/nursing/admissions/${admissionId}/handovers`, { signal }),
    }),
  intakeOutput: (admissionId: string, dutyDate: string) =>
    queryOptions({
      queryKey: nursingKeys.intakeOutput(admissionId, dutyDate),
      queryFn: ({ signal }) => api.get<IntakeOutputChart>(`/api/v1/nursing/admissions/${admissionId}/intake-output`, { query: { dutyDate }, signal }),
    }),
  assessments: (admissionId: string) =>
    queryOptions({
      queryKey: nursingKeys.assessments(admissionId),
      queryFn: ({ signal }) => api.get<AssessmentDto[]>(`/api/v1/nursing/admissions/${admissionId}/assessments`, { signal }),
    }),
  orders: (admissionId: string, activeOnly: boolean) =>
    queryOptions({
      queryKey: nursingKeys.orders(admissionId, activeOnly),
      queryFn: ({ signal }) =>
        api.get<MedicationOrderDto[]>(`/api/v1/nursing/admissions/${admissionId}/medication-orders`, { query: { activeOnly: activeOnly || undefined }, signal }),
    }),
  mar: (admissionId: string, date: string) =>
    queryOptions({
      queryKey: nursingKeys.mar(admissionId, date),
      queryFn: ({ signal }) => api.get<MarEntryDto[]>(`/api/v1/nursing/admissions/${admissionId}/mar`, { query: { date }, signal }),
      refetchInterval: 60_000,
    }),
  wardHandovers: (wardId: string, dutyDate: string, shift: NursingShift | null) =>
    queryOptions({
      queryKey: nursingKeys.wardHandovers(wardId, dutyDate, shift),
      queryFn: ({ signal }) => api.get<WardHandoverItem[]>(`/api/v1/nursing/wards/${wardId}/handovers`, { query: { dutyDate, shift }, signal }),
    }),
  wardMarDue: (wardId: string, withinMinutes = 60) =>
    queryOptions({
      queryKey: nursingKeys.wardMarDue(wardId, withinMinutes),
      queryFn: ({ signal }) => api.get<WardMarItem[]>(`/api/v1/nursing/wards/${wardId}/mar/due`, { query: { withinMinutes }, signal }),
      refetchInterval: 60_000,
    }),
};

// ------------------------------------------------------------------ commands

export const nursingApi = {
  addNote: (admissionId: string, body: { category: NursingNoteCategory; note: string; noteFor: string | null }) =>
    api.post<NursingNoteDto>(`/api/v1/nursing/admissions/${admissionId}/notes`, body),
  addHandover: (admissionId: string, body: AddHandoverRequest) => api.post<HandoverDto>(`/api/v1/nursing/admissions/${admissionId}/handovers`, body),
  acknowledgeHandover: (handoverId: string) => api.post<HandoverDto>(`/api/v1/nursing/handovers/${handoverId}/acknowledge`),
  addIntakeOutput: (admissionId: string, body: AddIntakeOutputRequest) =>
    api.post<IntakeOutputDto>(`/api/v1/nursing/admissions/${admissionId}/intake-output`, body),
  voidIntakeOutput: (entryId: string, reason: string) => api.post<void>(`/api/v1/nursing/intake-output/${entryId}/void`, { reason }),
  addMorse: (admissionId: string, body: MorseAssessmentRequest) => api.post<AssessmentDto>(`/api/v1/nursing/admissions/${admissionId}/assessments/morse`, body),
  addBraden: (admissionId: string, body: BradenAssessmentRequest) => api.post<AssessmentDto>(`/api/v1/nursing/admissions/${admissionId}/assessments/braden`, body),
  addPain: (admissionId: string, body: PainAssessmentRequest) => api.post<AssessmentDto>(`/api/v1/nursing/admissions/${admissionId}/assessments/pain`, body),
  addOrder: (admissionId: string, body: AddMedicationOrderRequest) =>
    api.post<MedicationOrderDto>(`/api/v1/nursing/admissions/${admissionId}/medication-orders`, body),
  stopOrder: (orderId: string, reason: string) => api.post<MedicationOrderDto>(`/api/v1/nursing/medication-orders/${orderId}/stop`, { reason }),
  extendSchedule: (orderId: string) => api.post<{ medicationOrderId: string; dosesAdded: number }>(`/api/v1/nursing/medication-orders/${orderId}/schedule`),
  addAdHocDose: (admissionId: string, body: AddAdHocDoseRequest) => api.post<MarEntryDto>(`/api/v1/nursing/admissions/${admissionId}/mar`, body),
  recordDose: (entryId: string, body: RecordAdministrationRequest) => api.post<MarEntryDto>(`/api/v1/nursing/mar/${entryId}/record`, body),
};
