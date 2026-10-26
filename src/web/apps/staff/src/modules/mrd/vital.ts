import type { StatusTone } from '@hc360/ui';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Births and deaths in the hospital (server `VitalRegistrationEndpoints`): a birth is recorded against the mother
// (Form 1, Form 3 for a still birth), a death with its medical certificate of cause of death (MCCD, Form 4). Each is
// reported to the Registrar of Births and Deaths within 21 days, through the Civil Registration System (CRS).

// ------------------------------------------------------------------------------------------------ enums (as strings)

export const VITAL_REPORT_STATUSES = ['Recorded', 'Reported', 'EnteredInError'] as const;
export type VitalReportStatus = (typeof VITAL_REPORT_STATUSES)[number];

export const BIRTH_OUTCOMES = ['LiveBirth', 'StillBirth'] as const;
export type BirthOutcome = (typeof BIRTH_OUTCOMES)[number];

export const BIRTH_SEXES = ['Male', 'Female', 'Indeterminate'] as const;
export type BirthSex = (typeof BIRTH_SEXES)[number];

/** Method of delivery, as Form 1's statistical part asks it. */
export const DELIVERY_METHODS = ['Natural', 'Caesarean', 'ForcepsOrVacuum'] as const;
export type DeliveryMethod = (typeof DELIVERY_METHODS)[number];

/** Who attended the delivery (Form 1, statistical part). */
export const BIRTH_ATTENDANTS = ['Doctor', 'NurseOrMidwife', 'Other'] as const;
export type BirthAttendant = (typeof BIRTH_ATTENDANTS)[number];

/** The manner of death the MCCD records; `PendingInvestigation` only for a medico-legal case. */
export const DEATH_MANNERS = ['Natural', 'Accident', 'Suicide', 'Homicide', 'PendingInvestigation'] as const;
export type DeathManner = (typeof DEATH_MANNERS)[number];

/** For a woman aged 15 to 49, whether the death came in pregnancy or soon after; anyone else is `NotApplicable`. */
export const PREGNANCIES_AT_DEATH = ['NotApplicable', 'NotPregnant', 'Pregnant', 'WithinSixWeeksOfDelivery'] as const;
export type PregnancyAtDeath = (typeof PREGNANCIES_AT_DEATH)[number];

// ------------------------------------------------------------------------------------------------ labels

// The same wording as the server's printed forms and register export (server `VitalRegistrationDocuments`).

export const vitalStatusLabel: Record<VitalReportStatus, string> = {
  Recorded: 'Not yet reported',
  Reported: 'Reported',
  EnteredInError: 'Entered in error',
};

export const vitalStatusTone: Record<VitalReportStatus, StatusTone> = {
  Recorded: 'warning',
  Reported: 'success',
  EnteredInError: 'informative',
};

export const birthOutcomeLabel: Record<BirthOutcome, string> = {
  LiveBirth: 'Live birth',
  StillBirth: 'Still birth',
};

export const birthSexLabel: Record<BirthSex, string> = {
  Male: 'Male',
  Female: 'Female',
  Indeterminate: 'Indeterminate',
};

export const deliveryMethodLabel: Record<DeliveryMethod, string> = {
  Natural: 'Natural',
  Caesarean: 'Caesarean',
  ForcepsOrVacuum: 'Forceps or vacuum',
};

export const birthAttendantLabel: Record<BirthAttendant, string> = {
  Doctor: 'Doctor',
  NurseOrMidwife: 'Nurse or midwife',
  Other: 'Other',
};

export const deathMannerLabel: Record<DeathManner, string> = {
  Natural: 'Natural',
  Accident: 'Accident',
  Suicide: 'Suicide',
  Homicide: 'Homicide',
  PendingInvestigation: 'Pending investigation',
};

export const pregnancyLabel: Record<PregnancyAtDeath, string> = {
  NotApplicable: 'Not applicable',
  NotPregnant: 'Not pregnant',
  Pregnant: 'Pregnant at the time of death',
  WithinSixWeeksOfDelivery: 'Within six weeks of the end of a pregnancy',
};

/** Part I of the MCCD, lines (a) to (d): the immediate cause first, the underlying cause last. */
export const causeLineLabel = ['(a) Immediate cause', '(b) Due to', '(c) Due to', '(d) Underlying cause'] as const;

// ------------------------------------------------------------------------------------------------ records

/** A birth in the hospital (server `BirthRecordDto`). */
export interface BirthRecordDto {
  id: string;
  /** The hospital's birth register number, from 1 each calendar year. */
  serialNumber: string;
  motherPatientId: string;
  motherUhid: string;
  motherName: string;
  babyPatientId: string | null;
  babyUhid: string | null;
  admissionId: string | null;
  status: VitalReportStatus;
  bornAt: string;
  outcome: BirthOutcome;
  sex: BirthSex;
  weightGrams: number | null;
  weeksOfGestation: number | null;
  method: DeliveryMethod;
  attendant: BirthAttendant;
  childName: string | null;
  fatherName: string | null;
  motherAgeYears: number;
  liveChildren: number | null;
  informantName: string;
  stillBirthCause: string | null;
  /** The last day to report it to the Registrar (21 days from the birth). */
  dueBy: string;
  reportedOn: string | null;
  registrationNumber: string | null;
  errorReason: string | null;
  /** Whether this user may mark it entered in error: it is still only recorded, and they recorded or certified it. */
  canMarkInError: boolean;
  version: number;
}

/** One line of Part I of the MCCD: a condition and how long before death it began ("2 days", "10 years"). */
export interface CauseOfDeath {
  condition: string;
  interval: string | null;
}

/** A death and its medical certificate of cause of death (server `DeathRecordDto`). */
export interface DeathRecordDto {
  id: string;
  serialNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: string | null;
  admissionId: string | null;
  status: VitalReportStatus;
  diedAt: string;
  ageYears: number | null;
  causes: CauseOfDeath[];
  underlyingCause: string;
  otherConditions: string | null;
  manner: DeathManner;
  medicoLegal: boolean;
  pregnancy: PregnancyAtDeath;
  certifiedBy: string;
  certifierRegistrationNumber: string;
  certifiedAt: string;
  dueBy: string;
  reportedOn: string | null;
  registrationNumber: string | null;
  errorReason: string | null;
  /** Whether this user may mark it entered in error: it is still only recorded, and they recorded or certified it. */
  canMarkInError: boolean;
  version: number;
}

/** A birth to record, or to correct (`version`: the record as read) until it is reported. */
export interface SaveBirthRequest {
  motherPatientId: string;
  babyPatientId: string | null;
  admissionId: string | null;
  bornAt: string;
  outcome: BirthOutcome;
  sex: BirthSex;
  weightGrams: number | null;
  weeksOfGestation: number | null;
  method: DeliveryMethod;
  attendant: BirthAttendant;
  childName: string | null;
  fatherName: string | null;
  motherAgeYears: number;
  liveChildren: number | null;
  informantName: string;
  stillBirthCause: string | null;
  version: number | null;
}

/**
 * A death's certificate to record, or to correct until it is reported. The certifying doctor is the signed-in user; the
 * form asks only for their registration number. `ageYears`: for a patient registered without a date of birth.
 */
export interface SaveDeathRequest {
  patientId: string;
  admissionId: string | null;
  diedAt: string;
  ageYears: number | null;
  causes: CauseOfDeath[];
  otherConditions: string | null;
  manner: DeathManner;
  medicoLegal: boolean;
  pregnancy: PregnancyAtDeath;
  certifierRegistrationNumber: string;
  version: number | null;
}

/** The Registrar's acknowledgement: the day it was reported and the registration number CRS gave. */
export interface ReportToRegistrarRequest {
  reportedOn: string;
  registrationNumber: string;
  version: number;
}

// ------------------------------------------------------------------------------------------------ registers

export interface BirthRegisterRow {
  id: string;
  serialNumber: string;
  bornAt: string;
  outcome: BirthOutcome;
  sex: BirthSex;
  weightGrams: number | null;
  motherName: string;
  motherUhid: string;
  fatherName: string | null;
  childName: string | null;
  status: VitalReportStatus;
  dueBy: string;
  /** Past the 21 days and still not reported. */
  overdue: boolean;
  reportedOn: string | null;
  registrationNumber: string | null;
}

export interface DeathRegisterRow {
  id: string;
  serialNumber: string;
  diedAt: string;
  patientName: string;
  uhid: string;
  gender: string | null;
  ageYears: number | null;
  underlyingCause: string;
  manner: DeathManner;
  medicoLegal: boolean;
  certifiedBy: string;
  status: VitalReportStatus;
  dueBy: string;
  overdue: boolean;
  reportedOn: string | null;
  registrationNumber: string | null;
}

/** The month's births, live and still, without those entered in error. */
export interface BirthRegister {
  year: number;
  month: number;
  rows: BirthRegisterRow[];
  liveBirths: number;
  stillBirths: number;
  overdue: number;
}

export interface DeathRegister {
  year: number;
  month: number;
  rows: DeathRegisterRow[];
  medicoLegal: number;
  overdue: number;
}

// ------------------------------------------------------------------------------------------------ queries

export type VitalKind = 'birth' | 'death';

export const vitalKeys = {
  all: ['mrd', 'vital'] as const,
  registers: (kind: VitalKind) => ['mrd', 'vital', kind, 'register'] as const,
  register: (kind: VitalKind, year: number, month: number) => ['mrd', 'vital', kind, 'register', year, month] as const,
  birth: (id: string) => ['mrd', 'vital', 'birth', 'detail', id] as const,
  death: (id: string) => ['mrd', 'vital', 'death', 'detail', id] as const,
};

const births = '/api/v1/mrd/births';
const deaths = '/api/v1/mrd/deaths';
const birth = (id: string) => `${births}/${id}`;
const death = (id: string) => `${deaths}/${id}`;

export const vitalQueries = {
  birthRegister: (year: number, month: number) =>
    queryOptions({
      queryKey: vitalKeys.register('birth', year, month),
      queryFn: ({ signal }) => api.get<BirthRegister>(births, { query: { year, month }, signal }),
      placeholderData: keepPreviousData,
    }),
  deathRegister: (year: number, month: number) =>
    queryOptions({
      queryKey: vitalKeys.register('death', year, month),
      queryFn: ({ signal }) => api.get<DeathRegister>(deaths, { query: { year, month }, signal }),
      placeholderData: keepPreviousData,
    }),
  birth: (id: string) =>
    queryOptions({
      queryKey: vitalKeys.birth(id),
      queryFn: ({ signal }) => api.get<BirthRecordDto>(birth(id), { signal }),
    }),
  death: (id: string) =>
    queryOptions({
      queryKey: vitalKeys.death(id),
      queryFn: ({ signal }) => api.get<DeathRecordDto>(death(id), { signal }),
    }),
};

// ------------------------------------------------------------------------------------------------ commands

export const vitalApi = {
  recordBirth: (request: SaveBirthRequest) => api.post<BirthRecordDto>(births, request),
  /** Corrects a birth until it is reported; `request.version` is the record as read (REL-04). */
  correctBirth: (id: string, request: SaveBirthRequest) => api.put<BirthRecordDto>(birth(id), request),
  reportBirth: (id: string, request: ReportToRegistrarRequest) =>
    api.post<BirthRecordDto>(`${birth(id)}/report`, request),
  birthInError: (id: string, reason: string, version: number) =>
    api.post<BirthRecordDto>(`${birth(id)}/entered-in-error`, { reason, version }),
  /** The month's birth register as CSV (the download is audited). */
  exportBirths: (year: number, month: number) => api.blob(`${births}/export`, { query: { year, month } }),

  certifyDeath: (request: SaveDeathRequest) => api.post<DeathRecordDto>(deaths, request),
  correctDeath: (id: string, request: SaveDeathRequest) => api.put<DeathRecordDto>(death(id), request),
  reportDeath: (id: string, request: ReportToRegistrarRequest) =>
    api.post<DeathRecordDto>(`${death(id)}/report`, request),
  deathInError: (id: string, reason: string, version: number) =>
    api.post<DeathRecordDto>(`${death(id)}/entered-in-error`, { reason, version }),
  exportDeaths: (year: number, month: number) => api.blob(`${deaths}/export`, { query: { year, month } }),
};

/** A birth's Form 1 (Form 3 for a still birth), and a death's MCCD, as the server prints them (`<path>/document`). */
export const birthDocument = (id: string) => birth(id);
export const deathDocument = (id: string) => death(id);

export const VITAL_PERMISSIONS = {
  /** Record and correct births, and mark one entered in error (doctors, nurses, medical records). */
  birthsRecord: 'mrd.births.record',
  /** Certify and correct deaths (the MCCD), and mark one entered in error (doctors). */
  deathsCertify: 'mrd.deaths.certify',
  /** The month's registers, their export and the report to the Registrar (medical records, administration). */
  register: 'mrd.vital.register',
} as const;

/** Who may open a birth (server `VitalRegistrationEndpoints.BirthReaders`); only whoever recorded it marks it entered in error. */
export const BIRTH_READERS = [VITAL_PERMISSIONS.birthsRecord, VITAL_PERMISSIONS.register] as const;
/** Who may open a death certificate (server `DeathReaders`); only the certifying doctor marks it entered in error. */
export const DEATH_READERS = [VITAL_PERMISSIONS.deathsCertify, VITAL_PERMISSIONS.register] as const;
