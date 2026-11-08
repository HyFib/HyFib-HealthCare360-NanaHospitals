import type { Gender } from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { AdmissionStatus, WardType } from './api';
import type { NursingShift } from './time';

// ------------------------------------------------------------------ NEWS2 (vital signs are Clinical's)

export type Consciousness = 'Alert' | 'NewConfusion' | 'Voice' | 'Pain' | 'Unresponsive';
export const CONSCIOUSNESS_LABEL: Record<Consciousness, string> = {
  Alert: 'A — Alert',
  NewConfusion: 'C — New confusion',
  Voice: 'V — Responds to voice',
  Pain: 'P — Responds to pain',
  Unresponsive: 'U — Unresponsive',
};
export const CONSCIOUSNESS_LEVELS = Object.keys(CONSCIOUSNESS_LABEL) as Consciousness[];

export type SpO2Scale = 'Scale1' | 'Scale2';
export type News2Response = 'Routine' | 'Urgent' | 'Emergency';

export interface News2Part {
  parameter: string;
  value: string;
  points: number;
}

export interface News2Score {
  total: number;
  anySingleParameterThree: boolean;
  response: News2Response;
  isComplete: boolean;
  scale: SpO2Scale;
  parts: News2Part[];
  missing: string[];
  summary: string;
}

/** The vital signs as Clinical returns them, with the fields this screen reads. */
export interface WardVitalsDto {
  id: string;
  recordedAt: string;
  temperatureCelsius: number | null;
  pulse: number | null;
  respiratoryRate: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  spO2: number | null;
  onSupplementalOxygen: boolean;
  consciousness: Consciousness | null;
  spO2Scale: SpO2Scale;
  news2: News2Score | null;
  isEnteredInError: boolean;
}

export interface RecordObservationsRequest {
  encounterId: null;
  recordedAt: string | null;
  temperature: number | null;
  temperatureUnit: 'Celsius';
  pulse: number | null;
  respiratoryRate: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  spO2: number | null;
  onSupplementalOxygen: boolean;
  heightCm: null;
  weightKg: null;
  painScore: null;
  bloodGlucose: null;
  glucoseTiming: 'Random';
  gcs: null;
  notes: string | null;
  consciousness: Consciousness | null;
  spO2Scale: SpO2Scale;
}

// ------------------------------------------------------------------ early-warning escalations

export type EscalationLevel = 'Urgent' | 'Emergency';
export type EscalationStatus = 'Open' | 'Responded';
export type EscalationOutcome =
  'ReviewedContinueObservation' | 'TreatmentChanged' | 'RapidResponseCalled' | 'TransferToCriticalCare';
export const ESCALATION_OUTCOME_LABEL: Record<EscalationOutcome, string> = {
  ReviewedContinueObservation: 'Reviewed: plan unchanged, closer observation',
  TreatmentChanged: 'Reviewed: treatment started or changed',
  RapidResponseCalled: 'Rapid response team called',
  TransferToCriticalCare: 'Moving to critical care',
};

export interface EscalationDto {
  id: string;
  admissionId: string;
  patientId: string;
  uhid: string;
  wardId: string | null;
  wardName: string | null;
  bedLabel: string | null;
  vitalSignsId: string;
  vitalsRecordedAt: string;
  score: number;
  anySingleParameterThree: boolean;
  isScoreComplete: boolean;
  summary: string;
  level: EscalationLevel;
  status: EscalationStatus;
  raisedAt: string;
  openMinutes: number | null;
  respondedAt: string | null;
  respondedByName: string | null;
  respondedByDoctor: boolean;
  outcome: EscalationOutcome | null;
  response: string | null;
  minutesToRespond: number | null;
  version: number;
  /** When the ward was reminded that nobody had responded (30 minutes after it was raised). */
  reminderSentAt?: string | null;
}

// ------------------------------------------------------------------ transfer requests

export type TransferRequestStatus = 'Pending' | 'Completed' | 'MovedAwaitingAcceptance' | 'Declined' | 'Cancelled';
export const TRANSFER_STATUS_LABEL: Record<TransferRequestStatus, string> = {
  Pending: 'Waiting for the receiving ward',
  Completed: 'Accepted, moved',
  MovedAwaitingAcceptance: 'Moved (urgent), acceptance due',
  Declined: 'Declined',
  Cancelled: 'Cancelled',
};

export interface TransferRequestDto {
  id: string;
  admissionId: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  fromWardId: string | null;
  fromWardName: string | null;
  fromBedLabel: string | null;
  toWardId: string;
  toWardName: string;
  toBedId: string;
  toBedLabel: string;
  reason: string;
  clinicalSummary: string;
  isUrgent: boolean;
  status: TransferRequestStatus;
  requestedAt: string;
  requestedByName: string | null;
  holdUntil: string | null;
  decidedAt: string | null;
  decidedByName: string | null;
  decisionNote: string | null;
  transferredAt: string | null;
  acceptanceDueAt: string | null;
  isAcceptanceOverdue: boolean;
  wasAcceptedLate: boolean;
  canDecide: boolean;
  version: number;
}

export interface RequestTransferBody {
  toBedId: string;
  reason: string;
  clinicalSummary: string;
  consultantPractitionerId: string | null;
  departmentId: string | null;
  isUrgent: boolean;
}

// ------------------------------------------------------------------ care plan

export type CarePlanStatus = 'Active' | 'Resolved';
export type CarePlanProgress = 'Improving' | 'Unchanged' | 'Worsening' | 'GoalMet';
export const CARE_PLAN_PROGRESS_LABEL: Record<CarePlanProgress, string> = {
  Improving: 'Improving',
  Unchanged: 'Unchanged',
  Worsening: 'Worsening',
  GoalMet: 'Goal met',
};

export interface CarePlanIntervention {
  text: string;
  frequency: string;
}

export interface CarePlanProblemTemplate {
  code: string;
  title: string;
  goal: string;
  interventions: CarePlanIntervention[];
}

export interface CarePlanEvaluationDto {
  id: string;
  dutyDate: string;
  shift: NursingShift;
  progress: CarePlanProgress;
  note: string | null;
  evaluatedAt: string;
  evaluatedByName: string | null;
}

export interface CarePlanProblemDto {
  id: string;
  admissionId: string;
  problemCode: string;
  title: string;
  goal: string;
  interventions: CarePlanIntervention[];
  status: CarePlanStatus;
  startedAt: string;
  startedByName: string | null;
  resolvedAt: string | null;
  resolvedByName: string | null;
  resolutionNote: string | null;
  lastEvaluation: CarePlanEvaluationDto | null;
  evaluatedThisShift: boolean;
  version: number;
}

// ------------------------------------------------------------------ downtime print

export type AllergyRecordingStatus = 'Recorded' | 'NoKnownAllergies' | 'NotRecorded';

export interface DowntimePatient {
  admissionId: string;
  admissionNumber: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  bedLabel: string | null;
  consultantName: string | null;
  admittedAt: string;
  dayOfStay: number;
  provisionalDiagnosis: string | null;
  isMlc: boolean;
  status: AdmissionStatus;
  allergyStatus: AllergyRecordingStatus;
  allergies: { allergen: string; severity: string; reaction: string | null }[];
  medications: {
    orderId: string;
    drugName: string;
    dose: string;
    route: string | null;
    frequency: string;
    isAsNeeded: boolean;
    isHighAlert: boolean;
    isControlledDrug: boolean;
    dosesHeld: boolean;
    dueTimes: string[];
  }[];
  news2: { score: number; response: News2Response; isComplete: boolean; recordedAt: string } | null;
  pendingTasks: string[];
  carePlan: { title: string; goal: string; interventions: CarePlanIntervention[] }[];
}

export interface DowntimeWard {
  wardId: string;
  code: string;
  name: string;
  type: WardType;
  counts: {
    total: number;
    occupied: number;
    vacant: number;
    reserved: number;
    cleaning: number;
    blocked: number;
    maintenance: number;
  };
  patients: DowntimePatient[];
}

export interface DowntimeWardPack {
  generatedAt: string;
  windowEndsAt: string;
  facilityName: string | null;
  printedByName: string | null;
  wards: DowntimeWard[];
}

// ------------------------------------------------------------------ queries and commands

export const wardCareKeys = {
  all: ['ward-care'] as const,
  vitals: (patientId: string) => ['ward-care', 'vitals', patientId] as const,
  escalations: (admissionId: string) => ['ward-care', 'escalations', admissionId] as const,
  wardEscalations: (wardId: string | null) => ['ward-care', 'ward-escalations', wardId ?? 'all'] as const,
  transfers: (wardId: string | null, openOnly: boolean) =>
    ['ward-care', 'transfers', wardId ?? 'all', openOnly] as const,
  carePlan: (admissionId: string) => ['ward-care', 'care-plan', admissionId] as const,
  carePlanProblems: ['ward-care', 'care-plan-problems'] as const,
};

export const wardCareQueries = {
  vitals: (patientId: string, from: string) =>
    queryOptions({
      queryKey: wardCareKeys.vitals(patientId),
      queryFn: ({ signal }) =>
        api
          .get<{ items: WardVitalsDto[] }>(`/api/v1/emr/patients/${patientId}/vitals`, {
            query: { from, pageSize: 50 },
            signal,
          })
          .then((page) => page.items),
    }),
  escalations: (admissionId: string) =>
    queryOptions({
      queryKey: wardCareKeys.escalations(admissionId),
      queryFn: ({ signal }) =>
        api.get<EscalationDto[]>(`/api/v1/nursing/admissions/${admissionId}/escalations`, { signal }),
    }),
  wardEscalations: (wardId: string | null) =>
    queryOptions({
      queryKey: wardCareKeys.wardEscalations(wardId),
      queryFn: ({ signal }) =>
        api.get<EscalationDto[]>('/api/v1/nursing/escalations', { query: { wardId: wardId ?? undefined }, signal }),
      refetchInterval: 60_000,
    }),
  transfers: (wardId: string | null, openOnly = true) =>
    queryOptions({
      queryKey: wardCareKeys.transfers(wardId, openOnly),
      queryFn: ({ signal }) =>
        api.get<TransferRequestDto[]>('/api/v1/inpatient/transfer-requests', {
          query: { wardId: wardId ?? undefined, openOnly },
          signal,
        }),
      refetchInterval: 60_000,
    }),
  carePlan: (admissionId: string) =>
    queryOptions({
      queryKey: wardCareKeys.carePlan(admissionId),
      queryFn: ({ signal }) =>
        api.get<CarePlanProblemDto[]>(`/api/v1/nursing/admissions/${admissionId}/care-plan`, { signal }),
    }),
  carePlanProblems: () =>
    queryOptions({
      queryKey: wardCareKeys.carePlanProblems,
      queryFn: ({ signal }) => api.get<CarePlanProblemTemplate[]>('/api/v1/nursing/care-plan/problems', { signal }),
      staleTime: Infinity,
    }),
};

export const wardCareApi = {
  recordObservations: (patientId: string, body: RecordObservationsRequest) =>
    api.post<WardVitalsDto>(`/api/v1/emr/patients/${patientId}/vitals`, body),
  respond: (escalationId: string, body: { outcome: EscalationOutcome; response: string; version: number }) =>
    api.post<EscalationDto>(`/api/v1/nursing/escalations/${escalationId}/respond`, body),
  requestTransfer: (admissionId: string, body: RequestTransferBody) =>
    api.post<TransferRequestDto>(`/api/v1/inpatient/admissions/${admissionId}/transfer-requests`, body),
  acceptTransfer: (id: string, body: { note: string | null; transferredAt: string | null; version: number }) =>
    api.post<TransferRequestDto>(`/api/v1/inpatient/transfer-requests/${id}/accept`, body),
  declineTransfer: (id: string, body: { reason: string; version: number }) =>
    api.post<TransferRequestDto>(`/api/v1/inpatient/transfer-requests/${id}/decline`, body),
  cancelTransfer: (id: string, body: { reason: string; version: number }) =>
    api.post<TransferRequestDto>(`/api/v1/inpatient/transfer-requests/${id}/cancel`, body),
  addProblem: (
    admissionId: string,
    body: {
      problemCode: string;
      title: string | null;
      goal: string | null;
      interventions: CarePlanIntervention[] | null;
    },
  ) => api.post<CarePlanProblemDto>(`/api/v1/nursing/admissions/${admissionId}/care-plan`, body),
  evaluate: (problemId: string, body: { progress: CarePlanProgress; note: string | null }) =>
    api.post<CarePlanProblemDto>(`/api/v1/nursing/care-plan/${problemId}/evaluate`, body),
  resolve: (problemId: string, body: { note: string; version: number }) =>
    api.post<CarePlanProblemDto>(`/api/v1/nursing/care-plan/${problemId}/resolve`, body),
  downtimePack: (wardId: string | null) =>
    api.get<DowntimeWardPack>('/api/v1/inpatient/downtime-print', { query: { wardId: wardId ?? undefined } }),
};

// ------------------------------------------------------------------ pure helpers

/** The badge tone for a NEWS2 response: red for emergency, amber for urgent, neutral otherwise. */
export function news2Tone(response: News2Response | null | undefined): 'danger' | 'warning' | 'informative' {
  return response === 'Emergency' ? 'danger' : response === 'Urgent' ? 'warning' : 'informative';
}

/** "NEWS2 6" with "(incomplete)" when not all seven parameters were taken. */
export function news2Label(score: number, isComplete: boolean): string {
  return `NEWS2 ${score}${isComplete ? '' : ' (incomplete)'}`;
}

/** What the response asks of the ward, in words. */
export function news2Advice(response: News2Response): string {
  switch (response) {
    case 'Emergency':
      return 'Emergency response: the ward doctor reviews at once and the rapid response team is called.';
    case 'Urgent':
      return 'Urgent review by the ward doctor; observations at least hourly.';
    default:
      return 'Routine monitoring.';
  }
}

/** An urgent move is one to or from critical care. */
export function isCriticalCareWard(type: WardType | null | undefined): boolean {
  return type === 'Icu' || type === 'Nicu' || type === 'Picu' || type === 'Hdu';
}
