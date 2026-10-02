import { isApiError, type Gender, type PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DeficiencyType } from '@/modules/mrd/api';
import type { PatientSummary, TokenPriority, TokenStatus } from '@/modules/patients/api';
import type { AppointmentStatus, AppointmentVisitType } from '@/modules/scheduling/api';
import type { DoseFrequency } from './dosing';
import type { EncounterContent } from './encounterForm';
import type { ServiceCategory } from './orderCart';
import type { TimelineEntry, TimelineEntryType } from './timeline';
import type { GlucoseTiming, RecordVitalsPayload, TemperatureUnit, VitalFlag } from './vitals';

// ---------------------------------------------------------------------------------------------------- enums

export type EncounterType = 'OutPatient' | 'InPatient' | 'Emergency' | 'Telehealth' | 'HealthCheckup';
export type EncounterStatus = 'InProgress' | 'Signed' | 'Amended';
export type ClinicalNoteType = 'ProgressNote' | 'Procedure' | 'Referral' | 'Counselling' | 'Other';
export type DiagnosticKind = 'Laboratory' | 'Radiology' | 'Cardiology';
export type AllergyType = 'Drug' | 'Food' | 'Environmental' | 'Other';
export type AllergySeverity = 'Mild' | 'Moderate' | 'Severe' | 'Anaphylaxis';
export type AllergyStatus = 'Active' | 'Inactive' | 'Resolved' | 'EnteredInError';
/**
 * What the chart may claim about allergies (server `AllergyRecordingStatus`): active allergies on the chart, a
 * clinician's "no known allergies" statement, or neither (inactive or resolved allergies alone count as not recorded).
 */
export type AllergyRecordingStatus = 'Recorded' | 'NoKnownAllergies' | 'NotRecorded';
export type PatientAlertType = 'Dnr' | 'FallRisk' | 'Isolation' | 'InfectionControl' | 'Pregnancy' | 'Custom';
export type AlertSeverity = 'Info' | 'Warning' | 'Critical';
export type DiagnosisType = 'Provisional' | 'Final' | 'Differential';
export type DiagnosisStatus = 'Active' | 'Resolved' | 'Chronic';
export type FavouriteKind = 'Order' | 'Drug';
export type DischargeSummaryStatus = 'Draft' | 'Signed' | 'Amended';
export type AdmissionStatus =
  'Admitted' | 'DischargeInitiated' | 'Discharged' | 'Cancelled' | 'Absconded' | 'Expired' | 'Lama' | 'Dama';
export type AdmissionType = 'Elective' | 'Emergency' | 'DayCare' | 'Maternity' | 'TransferIn';

export const ENCOUNTER_TYPES: readonly EncounterType[] = [
  'OutPatient',
  'Emergency',
  'InPatient',
  'Telehealth',
  'HealthCheckup',
];
export const encounterTypeLabel: Record<EncounterType, string> = {
  OutPatient: 'Out-patient (OP)',
  InPatient: 'In-patient',
  Emergency: 'Emergency',
  Telehealth: 'Teleconsultation',
  HealthCheckup: 'Health check-up',
};

export const NOTE_TYPES: readonly ClinicalNoteType[] = [
  'ProgressNote',
  'Procedure',
  'Referral',
  'Counselling',
  'Other',
];
export const noteTypeLabel: Record<ClinicalNoteType, string> = {
  ProgressNote: 'Progress note',
  Procedure: 'Procedure note',
  Referral: 'Referral letter',
  Counselling: 'Counselling note',
  Other: 'Other note',
};

export const ALLERGY_TYPES: readonly AllergyType[] = ['Drug', 'Food', 'Environmental', 'Other'];
export const ALLERGY_SEVERITIES: readonly AllergySeverity[] = ['Mild', 'Moderate', 'Severe', 'Anaphylaxis'];
export const ALERT_TYPES: readonly PatientAlertType[] = [
  'Dnr',
  'FallRisk',
  'Isolation',
  'InfectionControl',
  'Pregnancy',
  'Custom',
];
export const alertTypeLabel: Record<PatientAlertType, string> = {
  Dnr: 'Do not resuscitate (DNR)',
  FallRisk: 'Fall risk',
  Isolation: 'Isolation precautions',
  InfectionControl: 'Infection control',
  Pregnancy: 'Pregnant',
  Custom: 'Other alert',
};
export const DIAGNOSIS_TYPES: readonly DiagnosisType[] = ['Provisional', 'Final', 'Differential'];
export const DIAGNOSIS_STATUSES: readonly DiagnosisStatus[] = ['Active', 'Chronic', 'Resolved'];

// ---------------------------------------------------------------------------------------------------- worklist

export interface WorklistQueueEntry {
  tokenId: string;
  tokenLabel: string;
  tokenStatus: TokenStatus;
  priority: TokenPriority;
  visitId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  chiefComplaint: string | null;
  isFollowUp: boolean;
  issuedAt: string;
  waitingMinutes: number;
  encounterId: string | null;
  encounterStatus: EncounterStatus | null;
  hasActiveAllergies: boolean;
  hasCriticalAlerts: boolean;
}

/** A booking on the doctor's day; once the patient has checked in it carries the visit, token and note. */
export interface WorklistAppointment {
  id: string;
  number: string;
  startsAt: string;
  endsAt: string;
  status: AppointmentStatus;
  visitType: AppointmentVisitType;
  isTelehealth: boolean;
  reason: string | null;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  practitionerId: string;
  practitionerName: string | null;
  visitId: string | null;
  checkedInAt: string | null;
  tokenLabel: string | null;
  encounterId: string | null;
  encounterStatus: EncounterStatus | null;
  hasActiveAllergies: boolean;
  hasCriticalAlerts: boolean;
}

export interface WorklistEncounter {
  id: string;
  patientId: string;
  uhid: string;
  patientName: string;
  type: EncounterType;
  startedAt: string;
  chiefComplaint: string | null;
}

export interface WorklistNote {
  id: string;
  patientId: string;
  uhid: string;
  patientName: string;
  type: ClinicalNoteType;
  title: string | null;
  createdAt: string;
}

export interface WorklistPrescription {
  id: string;
  prescriptionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  prescribedAt: string;
  itemCount: number;
}

export interface WorklistResult {
  id: string;
  reportId: string;
  kind: DiagnosticKind;
  title: string;
  hasCriticalFindings: boolean;
  releasedAt: string;
  patientId: string;
  uhid: string;
  patientName: string;
}

export interface WorklistDeficiency {
  id: string;
  medicalRecordId: string;
  type: DeficiencyType;
  description: string;
  dueDate: string | null;
  patientId: string;
  uhid: string;
  patientName: string;
}

export interface EmrWorklist {
  date: string;
  practitionerId: string | null;
  departmentId: string | null;
  queue: WorklistQueueEntry[];
  /** Cancelled and rescheduled bookings are left out. */
  appointments: WorklistAppointment[];
  openEncounters: WorklistEncounter[];
  unsignedNotes: WorklistNote[];
  draftPrescriptions: WorklistPrescription[];
  resultsToReview: WorklistResult[];
  chartDeficiencies: WorklistDeficiency[];
}

export interface WorklistParams {
  date?: string;
  practitionerId?: string;
  departmentId?: string;
}

// ---------------------------------------------------------------------------------------------------- patient summary

export interface AllergySummary {
  id: string;
  allergen: string;
  type: AllergyType;
  severity: AllergySeverity;
  reaction: string | null;
}

export interface AlertSummary {
  id: string;
  type: PatientAlertType;
  title: string;
  detail: string | null;
  severity: AlertSeverity;
  expiresOn: string | null;
}

/**
 * The patient banner's safety flags alone (`GET /emr/patients/{id}/flags`, EHR-03): the same allergy status, active
 * allergies and current alerts as the summary, for staff who may see the flags but not the chart.
 */
export interface PatientFlags {
  allergyStatus: AllergyRecordingStatus;
  allergies: AllergySummary[];
  alerts: AlertSummary[];
}

export interface ProblemSummary {
  id: string;
  icdCode: string | null;
  title: string;
  type: DiagnosisType;
  status: DiagnosisStatus;
  onsetDate: string | null;
}

export interface CurrentMedication {
  prescriptionId: string;
  prescriptionNumber: string;
  itemId: string;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dose: string;
  frequency: string;
  durationDays: number;
  startedOn: string;
  endsOn: string;
  practitionerId: string;
  prescriberName: string | null;
  isControlledDrug: boolean;
}

export interface VitalsSnapshot {
  id: string;
  recordedAt: string;
  temperatureCelsius: number | null;
  pulse: number | null;
  respiratoryRate: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  spO2: number | null;
  weightKg: number | null;
  bmi: number | null;
  painScore: number | null;
  bloodGlucose: number | null;
  gcs: number | null;
  flags: VitalFlag[];
}

export interface RecentEncounter {
  id: string;
  type: EncounterType;
  status: EncounterStatus;
  startedAt: string;
  practitionerName: string | null;
  departmentName: string | null;
  chiefComplaint: string | null;
  diagnoses: string[];
}

export interface PatientClinicalSummary {
  patient: PatientSummary;
  allergiesEverRecorded: boolean;
  /** What the banner may claim; "no known allergies" only when a clinician recorded it, never from an empty list. */
  allergyStatus: AllergyRecordingStatus;
  allergies: AllergySummary[];
  alerts: AlertSummary[];
  activeProblems: ProblemSummary[];
  currentMedications: CurrentMedication[];
  lastVitals: VitalsSnapshot | null;
  recentEncounters: RecentEncounter[];
  resultsAwaitingReview: number;
}

export interface TimelineFilters {
  types?: TimelineEntryType[];
  from?: string;
  to?: string;
  departmentId?: string;
  page?: number;
}

// ---------------------------------------------------------------------------------------------------- encounters

export interface EncounterListItem {
  id: string;
  patientId: string;
  type: EncounterType;
  status: EncounterStatus;
  startedAt: string;
  signedAt: string | null;
  practitionerId: string;
  practitionerName: string | null;
  departmentId: string | null;
  departmentName: string | null;
  chiefComplaint: string | null;
  diagnoses: string[];
}

export interface EncounterAmendment {
  id: string;
  amendedAt: string;
  amendedByUserId: string | null;
  reason: string;
  previousContent: string;
}

export interface EncounterDetail extends EncounterContent {
  id: string;
  patientId: string;
  visitId: string | null;
  admissionId: string | null;
  practitionerId: string;
  practitionerName: string | null;
  departmentId: string | null;
  departmentName: string | null;
  type: EncounterType;
  status: EncounterStatus;
  startedAt: string;
  templateId: string | null;
  signedAt: string | null;
  signedByPractitionerId: string | null;
  amendmentReason: string | null;
  amendedAt: string | null;
  amendments: EncounterAmendment[];
  version: number;
}

export interface StartEncounterRequest {
  patientId: string;
  visitId: string | null;
  admissionId: string | null;
  type: EncounterType | null;
  departmentId: string | null;
  practitionerId: string | null;
  templateId: string | null;
  chiefComplaint: string | null;
}

// ---------------------------------------------------------------------------------------------------- vitals, flags, diagnoses, notes, immunisations

export interface VitalsDto {
  id: string;
  patientId: string;
  encounterId: string | null;
  recordedAt: string;
  recordedByUserId: string | null;
  temperature: number | null;
  temperatureUnit: TemperatureUnit;
  temperatureCelsius: number | null;
  pulse: number | null;
  respiratoryRate: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  spO2: number | null;
  onSupplementalOxygen: boolean;
  heightCm: number | null;
  weightKg: number | null;
  bmi: number | null;
  painScore: number | null;
  bloodGlucose: number | null;
  glucoseTiming: GlucoseTiming;
  gcs: number | null;
  notes: string | null;
  isEnteredInError: boolean;
  enteredInErrorReason: string | null;
  flags: VitalFlag[];
}

export interface AllergyDto {
  id: string;
  patientId: string;
  allergen: string;
  type: AllergyType;
  reaction: string | null;
  severity: AllergySeverity;
  status: AllergyStatus;
  statusReason: string | null;
  onsetDate: string | null;
  recordedAt: string;
  recordedByUserId: string | null;
  notes: string | null;
  version: number;
}

export interface RecordAllergyRequest {
  allergen: string;
  type: AllergyType;
  reaction: string | null;
  severity: AllergySeverity;
  onsetDate: string | null;
  notes: string | null;
}

export interface UpdateAllergyRequest {
  reaction: string | null;
  severity: AllergySeverity;
  status: AllergyStatus;
  statusReason: string | null;
  notes: string | null;
  version: number;
}

/**
 * The chart's allergy status (`GET …/allergies/status`, also returned by `POST …/allergies/none`). `recordedAt` and
 * `recordedByName` describe the latest active allergy (`Recorded`) or the "no known allergies" statement; both are
 * null for `NotRecorded`.
 */
export interface AllergyStatusDto {
  status: AllergyRecordingStatus;
  recordedAt: string | null;
  recordedByName: string | null;
}

export interface PatientAlertDto {
  id: string;
  patientId: string;
  type: PatientAlertType;
  title: string;
  detail: string | null;
  severity: AlertSeverity;
  effectiveFrom: string;
  expiresOn: string | null;
  isActive: boolean;
  isCurrent: boolean;
  deactivatedAt: string | null;
  deactivationReason: string | null;
}

export interface RaiseAlertRequest {
  type: PatientAlertType;
  title: string | null;
  detail: string | null;
  severity: AlertSeverity | null;
  expiresOn: string | null;
}

export interface DiagnosisDto {
  id: string;
  patientId: string;
  encounterId: string | null;
  icdCode: string | null;
  title: string;
  type: DiagnosisType;
  isPrimary: boolean;
  onsetDate: string | null;
  status: DiagnosisStatus;
  resolvedOn: string | null;
  recordedAt: string;
  notes: string | null;
  version: number;
}

export interface AddDiagnosisRequest {
  icdCode: string | null;
  title: string | null;
  type: DiagnosisType;
  isPrimary: boolean;
  onsetDate: string | null;
  status: DiagnosisStatus;
  notes: string | null;
}

export interface UpdateDiagnosisRequest {
  type: DiagnosisType;
  isPrimary: boolean;
  status: DiagnosisStatus;
  onsetDate: string | null;
  notes: string | null;
  version: number;
}

export interface ClinicalNoteDto {
  id: string;
  patientId: string;
  encounterId: string | null;
  admissionId: string | null;
  type: ClinicalNoteType;
  title: string | null;
  body: string;
  authorUserId: string | null;
  authorPractitionerId: string | null;
  authorName: string | null;
  amendsNoteId: string | null;
  isSigned: boolean;
  signedAt: string | null;
  createdAt: string;
  version: number;
}

export interface WriteNoteRequest {
  encounterId: string | null;
  admissionId: string | null;
  type: ClinicalNoteType;
  title: string | null;
  body: string;
}

export interface ImmunizationDto {
  id: string;
  patientId: string;
  encounterId: string | null;
  vaccineName: string;
  vaccineCode: string | null;
  doseNumber: number | null;
  administeredOn: string;
  route: string | null;
  site: string | null;
  batchNumber: string | null;
  manufacturer: string | null;
  expiryDate: string | null;
  administeredBy: string | null;
  isExternal: boolean;
  nextDueOn: string | null;
  notes: string | null;
  isEnteredInError: boolean;
  enteredInErrorReason: string | null;
}

export type RecordImmunizationRequest = Omit<
  ImmunizationDto,
  'id' | 'patientId' | 'isEnteredInError' | 'enteredInErrorReason'
>;

// ---------------------------------------------------------------------------------------------------- templates and favourites

export interface TemplateListItem {
  id: string;
  code: string;
  name: string;
  specialtyCode: string | null;
  isPersonal: boolean;
  displayOrder: number;
  isActive: boolean;
}

export interface TemplateServiceDto {
  serviceItemId: string;
  code: string;
  name: string;
  category: ServiceCategory;
}

export interface TemplateMedicationDto {
  id: string;
  drugItemId: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string;
  frequency: DoseFrequency;
  customFrequency: string | null;
  durationDays: number;
  instructions: string | null;
}

export interface TemplateDto {
  id: string;
  code: string;
  name: string;
  specialtyCode: string | null;
  ownerPractitionerId: string | null;
  chiefComplaint: string | null;
  historyOfPresentIllness: string | null;
  examination: string | null;
  assessment: string | null;
  plan: string | null;
  followUpAdvice: string | null;
  followUpDays: number | null;
  commonComplaints: string[];
  defaultOrders: TemplateServiceDto[];
  medications: TemplateMedicationDto[];
  displayOrder: number;
  isActive: boolean;
  version: number;
}

export interface FavouriteDto {
  id: string;
  kind: FavouriteKind;
  label: string;
  serviceItemId: string | null;
  drugItemId: string | null;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string | null;
  frequency: DoseFrequency | null;
  customFrequency: string | null;
  durationDays: number | null;
  instructions: string | null;
  isControlledDrug: boolean;
  displayOrder: number;
}

export type SaveFavouriteRequest = Omit<FavouriteDto, 'id'>;

// ---------------------------------------------------------------------------------------------------- admissions and discharge summaries

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
  dischargeType: string | null;
  lengthOfStayDays: number;
}

export interface DischargeContent {
  diagnoses: string | null;
  diagnosisCodes: string[];
  presentingComplaints: string | null;
  courseInHospital: string | null;
  procedures: string | null;
  investigationsSummary: string | null;
  conditionAtDischarge: string | null;
  medications: string | null;
  advice: string | null;
  followUpDate: string | null;
  followUpInstructions: string | null;
}

export interface DischargeSummaryDto extends DischargeContent {
  id: string;
  admissionId: string;
  patientId: string;
  encounterId: string | null;
  practitionerId: string | null;
  practitionerName: string | null;
  admittedAt: string | null;
  dischargedAt: string | null;
  status: DischargeSummaryStatus;
  signedAt: string | null;
  signedByPractitionerId: string | null;
  amendmentReason: string | null;
  amendedAt: string | null;
  amendmentCount: number;
  version: number;
}

export interface SaveDischargeSummaryRequest extends DischargeContent {
  patientId: string;
  encounterId: string | null;
  practitionerId: string | null;
  admittedAt: string | null;
  dischargedAt: string | null;
  version: number | null;
}

export interface DischargeSummaryPrefill {
  diagnoses: string | null;
  diagnosisCodes: string[];
  presentingComplaints: string | null;
  procedures: string | null;
  investigationsSummary: string | null;
  medications: string | null;
  followUpDate: string | null;
  followUpInstructions: string | null;
}

// ---------------------------------------------------------------------------------------------------- results (lab report view subset)

export type LabResultFlag = 'Normal' | 'Low' | 'High' | 'CriticalLow' | 'CriticalHigh' | 'Abnormal' | 'Critical';

export interface LabReportResultLine {
  resultId: string;
  name: string;
  groupName: string | null;
  value: string | null;
  unit: string | null;
  flag: LabResultFlag | null;
  referenceText: string | null;
  remarks: string | null;
}

export interface LabReportView {
  reportId: string;
  reportNumber: string;
  status: string;
  isPartial: boolean;
  releasedAt: string | null;
  sections: {
    section: string;
    tests: { orderItemId: string; name: string; interpretation: string | null; results: LabReportResultLine[] }[];
  }[];
}

// ---------------------------------------------------------------------------------------------------- query keys and queries

export const emrKeys = {
  all: ['emr'] as const,
  worklist: (params: WorklistParams) => ['emr', 'worklist', params] as const,
  patient: (patientId: string) => ['emr', 'patient', patientId] as const,
  summary: (patientId: string) => ['emr', 'patient', patientId, 'summary'] as const,
  /** Under the patient key, so every allergy or alert change made on the chart refreshes the banners too. */
  flags: (patientId: string) => ['emr', 'patient', patientId, 'flags'] as const,
  timeline: (patientId: string, filters: TimelineFilters) =>
    ['emr', 'patient', patientId, 'timeline', filters] as const,
  encounters: (patientId: string, page: number) => ['emr', 'patient', patientId, 'encounters', page] as const,
  vitals: (patientId: string, encounterId: string | null, page: number, pageSize: number) =>
    ['emr', 'patient', patientId, 'vitals', encounterId, page, pageSize] as const,
  allergies: (patientId: string, includeInactive: boolean) =>
    ['emr', 'patient', patientId, 'allergies', includeInactive] as const,
  /** Under the patient key, so every allergy change (which invalidates `patient`) refreshes it with the summary. */
  allergyStatus: (patientId: string) => ['emr', 'patient', patientId, 'allergy-status'] as const,
  alerts: (patientId: string, includeInactive: boolean) =>
    ['emr', 'patient', patientId, 'alerts', includeInactive] as const,
  diagnoses: (patientId: string, encounterId: string | null, problemsOnly: boolean) =>
    ['emr', 'patient', patientId, 'diagnoses', encounterId, problemsOnly] as const,
  notes: (patientId: string, encounterId: string | null, page: number) =>
    ['emr', 'patient', patientId, 'notes', encounterId, page] as const,
  immunizations: (patientId: string, includeErrors: boolean) =>
    ['emr', 'patient', patientId, 'immunizations', includeErrors] as const,
  admissions: (patientId: string) => ['emr', 'patient', patientId, 'admissions'] as const,
  encounter: (id: string) => ['emr', 'encounter', id] as const,
  templates: (specialtyCode: string | null) => ['emr', 'templates', specialtyCode ?? 'all'] as const,
  template: (id: string) => ['emr', 'template', id] as const,
  favourites: (kind: FavouriteKind) => ['emr', 'favourites', kind] as const,
  discharge: (admissionId: string) => ['emr', 'discharge', admissionId] as const,
  labReport: (reportId: string) => ['emr', 'lab-report', reportId] as const,
};

export const emrQueries = {
  worklist: (params: WorklistParams) =>
    queryOptions({
      queryKey: emrKeys.worklist(params),
      queryFn: ({ signal }) => api.get<EmrWorklist>('/api/v1/emr/worklist', { query: { ...params }, signal }),
      refetchInterval: 60_000,
      placeholderData: keepPreviousData,
    }),
  summary: (patientId: string) =>
    queryOptions({
      queryKey: emrKeys.summary(patientId),
      queryFn: ({ signal }) => api.get<PatientClinicalSummary>(`/api/v1/emr/patients/${patientId}/summary`, { signal }),
      staleTime: 15_000,
    }),
  flags: (patientId: string) =>
    queryOptions({
      queryKey: emrKeys.flags(patientId),
      queryFn: ({ signal }) => api.get<PatientFlags>(`/api/v1/emr/patients/${patientId}/flags`, { signal }),
      // Changes arrive at once over real time; each fetch is an audited read, so moving between a patient's screens or
      // back to the window within a minute reuses the last answer.
      staleTime: 60_000,
    }),
  timeline: (patientId: string, filters: TimelineFilters) =>
    queryOptions({
      queryKey: emrKeys.timeline(patientId, filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<TimelineEntry>>(`/api/v1/emr/patients/${patientId}/timeline`, {
          query: {
            types: filters.types && filters.types.length > 0 ? filters.types.join(',') : undefined,
            from: filters.from,
            to: filters.to,
            departmentId: filters.departmentId,
            page: filters.page,
            pageSize: 30,
          },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  encounters: (patientId: string, page = 1) =>
    queryOptions({
      queryKey: emrKeys.encounters(patientId, page),
      queryFn: ({ signal }) =>
        api.get<PagedResult<EncounterListItem>>(`/api/v1/emr/patients/${patientId}/encounters`, {
          query: { page, pageSize: 15 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  vitals: (patientId: string, encounterId: string | null = null, page = 1, pageSize = 20) =>
    queryOptions({
      queryKey: emrKeys.vitals(patientId, encounterId, page, pageSize),
      queryFn: ({ signal }) =>
        api.get<PagedResult<VitalsDto>>(`/api/v1/emr/patients/${patientId}/vitals`, {
          query: { encounterId, page, pageSize },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  allergies: (patientId: string, includeInactive = false) =>
    queryOptions({
      queryKey: emrKeys.allergies(patientId, includeInactive),
      queryFn: ({ signal }) =>
        api.get<AllergyDto[]>(`/api/v1/emr/patients/${patientId}/allergies`, { query: { includeInactive }, signal }),
    }),
  allergyStatus: (patientId: string) =>
    queryOptions({
      queryKey: emrKeys.allergyStatus(patientId),
      queryFn: ({ signal }) =>
        api.get<AllergyStatusDto>(`/api/v1/emr/patients/${patientId}/allergies/status`, { signal }),
    }),
  alerts: (patientId: string, includeInactive = false) =>
    queryOptions({
      queryKey: emrKeys.alerts(patientId, includeInactive),
      queryFn: ({ signal }) =>
        api.get<PatientAlertDto[]>(`/api/v1/emr/patients/${patientId}/alerts`, { query: { includeInactive }, signal }),
    }),
  diagnoses: (patientId: string, encounterId: string | null = null, problemsOnly = false) =>
    queryOptions({
      queryKey: emrKeys.diagnoses(patientId, encounterId, problemsOnly),
      queryFn: ({ signal }) =>
        api.get<DiagnosisDto[]>(`/api/v1/emr/patients/${patientId}/diagnoses`, {
          query: { encounterId, problemsOnly: problemsOnly || undefined },
          signal,
        }),
    }),
  notes: (patientId: string, encounterId: string | null = null, page = 1) =>
    queryOptions({
      queryKey: emrKeys.notes(patientId, encounterId, page),
      queryFn: ({ signal }) =>
        api.get<PagedResult<ClinicalNoteDto>>(`/api/v1/emr/patients/${patientId}/notes`, {
          query: { encounterId, page, pageSize: 20 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  immunizations: (patientId: string, includeErrors = false) =>
    queryOptions({
      queryKey: emrKeys.immunizations(patientId, includeErrors),
      queryFn: ({ signal }) =>
        api.get<ImmunizationDto[]>(`/api/v1/emr/patients/${patientId}/immunizations`, {
          query: { includeEnteredInError: includeErrors || undefined },
          signal,
        }),
    }),
  admissions: (patientId: string) =>
    queryOptions({
      queryKey: emrKeys.admissions(patientId),
      queryFn: ({ signal }) => api.get<PatientAdmissionItem[]>(`/api/v1/patients/${patientId}/admissions`, { signal }),
    }),
  encounter: (id: string) =>
    queryOptions({
      queryKey: emrKeys.encounter(id),
      queryFn: ({ signal }) => api.get<EncounterDetail>(`/api/v1/emr/encounters/${id}`, { signal }),
    }),
  templates: (specialtyCode: string | null = null) =>
    queryOptions({
      queryKey: emrKeys.templates(specialtyCode),
      queryFn: ({ signal }) =>
        api.get<TemplateListItem[]>('/api/v1/emr/templates', { query: { specialtyCode }, signal }),
      staleTime: 5 * 60_000,
    }),
  template: (id: string) =>
    queryOptions({
      queryKey: emrKeys.template(id),
      queryFn: ({ signal }) => api.get<TemplateDto>(`/api/v1/emr/templates/${id}`, { signal }),
      staleTime: 5 * 60_000,
    }),
  favourites: (kind: FavouriteKind) =>
    queryOptions({
      queryKey: emrKeys.favourites(kind),
      queryFn: ({ signal }) => api.get<FavouriteDto[]>('/api/v1/emr/favourites', { query: { kind }, signal }),
      staleTime: 5 * 60_000,
    }),
  discharge: (admissionId: string) =>
    queryOptions({
      queryKey: emrKeys.discharge(admissionId),
      // 404 means "no summary yet"; the editor then starts a new draft.
      queryFn: async ({ signal }) => {
        try {
          return await api.get<DischargeSummaryDto>(
            `/api/v1/clinical/discharge-summaries/by-admission/${admissionId}`,
            { signal },
          );
        } catch (error) {
          if (isApiError(error) && error.status === 404) {
            return null;
          }
          throw error;
        }
      },
    }),
  labReport: (reportId: string) =>
    queryOptions({
      queryKey: emrKeys.labReport(reportId),
      queryFn: ({ signal }) => api.get<LabReportView>(`/api/v1/lab/reports/${reportId}`, { signal }),
      staleTime: 60_000,
    }),
};

// ---------------------------------------------------------------------------------------------------- commands

export const emrApi = {
  acknowledgeResult: (id: string, note: string | null) =>
    api.post<void>(`/api/v1/emr/results/${id}/acknowledge`, { note }),

  startEncounter: (request: StartEncounterRequest) => api.post<EncounterDetail>('/api/v1/emr/encounters', request),
  updateEncounter: (id: string, content: EncounterContent, version: number) =>
    api.put<EncounterDetail>(`/api/v1/emr/encounters/${id}`, { ...content, version }),
  signEncounter: (id: string, version: number) =>
    api.post<EncounterDetail>(`/api/v1/emr/encounters/${id}/sign`, { version }),
  amendEncounter: (id: string, reason: string, content: EncounterContent, version: number) =>
    api.post<EncounterDetail>(`/api/v1/emr/encounters/${id}/amend`, { reason, ...content, version }),

  recordVitals: (patientId: string, request: RecordVitalsPayload) =>
    api.post<VitalsDto>(`/api/v1/emr/patients/${patientId}/vitals`, request),
  previewVitals: (patientId: string, request: RecordVitalsPayload, signal?: AbortSignal) =>
    api.post<VitalFlag[]>(`/api/v1/emr/patients/${patientId}/vitals/preview`, request, { signal }),
  vitalsEnteredInError: (id: string, reason: string) =>
    api.post<VitalsDto>(`/api/v1/emr/vitals/${id}/entered-in-error`, { reason }),

  recordAllergy: (patientId: string, request: RecordAllergyRequest) =>
    api.post<AllergyDto>(`/api/v1/emr/patients/${patientId}/allergies`, request),
  updateAllergy: (id: string, request: UpdateAllergyRequest) =>
    api.put<AllergyDto>(`/api/v1/emr/allergies/${id}`, request),
  /** Idempotent; 409 `allergy.active_exist` (with `data.activeAllergies`) while the patient has active allergies. */
  recordNoKnownAllergies: (patientId: string) =>
    api.post<AllergyStatusDto>(`/api/v1/emr/patients/${patientId}/allergies/none`),
  raiseAlert: (patientId: string, request: RaiseAlertRequest) =>
    api.post<PatientAlertDto>(`/api/v1/emr/patients/${patientId}/alerts`, request),
  deactivateAlert: (id: string, reason: string) =>
    api.post<PatientAlertDto>(`/api/v1/emr/alerts/${id}/deactivate`, { reason }),

  addDiagnosis: (encounterId: string, request: AddDiagnosisRequest) =>
    api.post<DiagnosisDto>(`/api/v1/emr/encounters/${encounterId}/diagnoses`, request),
  updateDiagnosis: (id: string, request: UpdateDiagnosisRequest) =>
    api.put<DiagnosisDto>(`/api/v1/emr/diagnoses/${id}`, request),

  writeNote: (patientId: string, request: WriteNoteRequest) =>
    api.post<ClinicalNoteDto>(`/api/v1/emr/patients/${patientId}/notes`, request),
  editNote: (id: string, request: { type: ClinicalNoteType; title: string | null; body: string; version: number }) =>
    api.put<ClinicalNoteDto>(`/api/v1/emr/notes/${id}`, request),
  signNote: (id: string, version: number) => api.post<ClinicalNoteDto>(`/api/v1/emr/notes/${id}/sign`, { version }),
  addendum: (id: string, body: string) => api.post<ClinicalNoteDto>(`/api/v1/emr/notes/${id}/addendum`, { body }),

  recordImmunization: (patientId: string, request: RecordImmunizationRequest) =>
    api.post<ImmunizationDto>(`/api/v1/emr/patients/${patientId}/immunizations`, request),
  immunizationEnteredInError: (id: string, reason: string) =>
    api.post<ImmunizationDto>(`/api/v1/emr/immunizations/${id}/entered-in-error`, { reason }),

  addFavourite: (request: SaveFavouriteRequest) => api.post<FavouriteDto>('/api/v1/emr/favourites', request),
  removeFavourite: (id: string) => api.delete<void>(`/api/v1/emr/favourites/${id}`),

  saveDischargeSummary: (admissionId: string, request: SaveDischargeSummaryRequest) =>
    api.put<DischargeSummaryDto>(`/api/v1/clinical/discharge-summaries/by-admission/${admissionId}`, request),
  prefillDischargeSummary: (admissionId: string, patientId: string) =>
    api.get<DischargeSummaryPrefill>(`/api/v1/clinical/discharge-summaries/by-admission/${admissionId}/prefill`, {
      query: { patientId },
    }),
  signDischargeSummary: (id: string, version: number) =>
    api.post<DischargeSummaryDto>(`/api/v1/clinical/discharge-summaries/${id}/sign`, { version }),
  amendDischargeSummary: (id: string, reason: string, content: DischargeContent, version: number) =>
    api.post<DischargeSummaryDto>(`/api/v1/clinical/discharge-summaries/${id}/amend`, { reason, ...content, version }),
};

/** The A4 discharge summary as the server prints it and makes its PDF (see `@/lib/documents`). */
export const dischargeSummaryDocument = (id: string) => `/api/v1/clinical/discharge-summaries/${id}`;
