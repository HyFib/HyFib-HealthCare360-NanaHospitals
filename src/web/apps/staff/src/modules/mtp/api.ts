import type { StatusTone } from '@hc360/ui';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// ------------------------------------------------------------------------------------------------ enums (as strings)

export const MTP_CASE_STATUSES = ['Certified', 'Terminated', 'Cancelled'] as const;
export type MtpCaseStatus = (typeof MTP_CASE_STATUSES)[number];

/** The grounds of s.3(2) of the MTP Act, in the server's order (server `MtpGround`). */
export const MTP_GROUNDS = [
  'RiskToLife',
  'InjuryToPhysicalHealth',
  'InjuryToMentalHealth',
  'Rape',
  'ContraceptiveFailure',
  'FoetalAbnormality',
] as const;
export type MtpGround = (typeof MTP_GROUNDS)[number];

/** The women for whom a termination from 20 to 24 weeks may be done (MTP Rules, rule 3B; server `MtpRule3BCategory`). */
export const MTP_CATEGORIES = [
  'SexualAssaultRapeOrIncest',
  'Minor',
  'ChangeOfMaritalStatus',
  'PhysicalDisability',
  'MentalIllness',
  'FoetalMalformation',
  'HumanitarianSettingOrEmergency',
] as const;
export type MtpRule3BCategory = (typeof MTP_CATEGORIES)[number];

export const MTP_METHODS = ['Medical', 'VacuumAspiration', 'DilatationAndEvacuation', 'Other'] as const;
export type MtpMethod = (typeof MTP_METHODS)[number];

/** Whose written consent was taken (Form C): hers, or her guardian's when she is under 18 or mentally ill. */
export type MtpConsentBy = 'Woman' | 'Guardian';

// ------------------------------------------------------------------------------------------------ labels

// The same wording as the server's printed forms and register export (server `MtpLabels`).

export const mtpGroundLabel: Record<MtpGround, string> = {
  RiskToLife: 'Risk to the life of the pregnant woman',
  InjuryToPhysicalHealth: 'Grave injury to her physical health',
  InjuryToMentalHealth: 'Grave injury to her mental health',
  Rape: 'Pregnancy caused by rape',
  ContraceptiveFailure: 'Failure of a contraceptive device or method',
  FoetalAbnormality: 'Substantial risk of a serious foetal abnormality',
};

export const mtpCategoryLabel: Record<MtpRule3BCategory, string> = {
  SexualAssaultRapeOrIncest: 'Survivor of sexual assault, rape or incest',
  Minor: 'Minor',
  ChangeOfMaritalStatus: 'Change of marital status during the pregnancy (widowhood or divorce)',
  PhysicalDisability: 'Woman with a physical disability',
  MentalIllness: 'Mentally ill woman, including with mental retardation',
  FoetalMalformation: 'Foetal malformation incompatible with life or with serious handicap',
  HumanitarianSettingOrEmergency: 'Woman in a humanitarian setting, disaster or emergency',
};

export const mtpMethodLabel: Record<MtpMethod, string> = {
  Medical: 'Medical method of abortion',
  VacuumAspiration: 'Vacuum aspiration',
  DilatationAndEvacuation: 'Dilatation and evacuation',
  Other: 'Other',
};

export const mtpStatusLabel: Record<MtpCaseStatus, string> = {
  Certified: 'Certified, not yet done',
  Terminated: 'Terminated',
  Cancelled: 'Cancelled',
};

export const mtpStatusTone: Record<MtpCaseStatus, StatusTone> = {
  Certified: 'warning',
  Terminated: 'success',
  Cancelled: 'informative',
};

// ------------------------------------------------------------------------------------------------ cases

/** One doctor who formed the opinion of Form I. */
export interface MtpOpinion {
  doctorName: string;
  registrationNumber: string;
  opinionOn: string;
}

/** A termination of pregnancy (server `MtpCaseDto`). */
export interface MtpCaseDto {
  id: string;
  /** The serial number of the admission register (Form III). */
  serialNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  admissionId: string | null;
  status: MtpCaseStatus;
  /** The place's certificate of approval (Form B) when the case was certified. */
  approvalNumber: string;
  ageYears: number;
  mentallyIll: boolean;
  consentBy: MtpConsentBy;
  guardianName: string | null;
  guardianRelation: string | null;
  consentOn: string;
  consentFileId: string | null;
  weeksOfGestation: number;
  grounds: MtpGround[];
  category: MtpRule3BCategory | null;
  medicalBoardReference: string | null;
  opinions: MtpOpinion[];
  certifiedAt: string;
  certifiedBy: string | null;
  terminatedOn: string | null;
  method: MtpMethod | null;
  performedBy: string | null;
  dischargedOn: string | null;
  outcome: string | null;
  remarks: string | null;
  cancelReason: string | null;
  version: number;
}

export interface MtpCaseListItem {
  id: string;
  serialNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  status: MtpCaseStatus;
  weeksOfGestation: number;
  certifiedAt: string;
  terminatedOn: string | null;
}

/** The woman's consent (Form C) and the doctors' opinion (Form I); without `version` a new case is opened. */
export interface SaveMtpCaseRequest {
  patientId: string;
  admissionId: string | null;
  ageYears: number;
  mentallyIll: boolean;
  consentBy: MtpConsentBy;
  guardianName: string | null;
  guardianRelation: string | null;
  consentOn: string;
  consentFileId: string | null;
  weeksOfGestation: number;
  grounds: MtpGround[];
  category: MtpRule3BCategory | null;
  medicalBoardReference: string | null;
  opinions: MtpOpinion[];
  /** The case as it was read, when correcting it (REL-04); null opens a new case. */
  version: number | null;
}

export interface RecordMtpTerminationRequest {
  terminatedOn: string;
  method: MtpMethod;
  performedBy: string;
  dischargedOn: string | null;
  outcome: string | null;
  remarks: string | null;
  version: number;
}

// ------------------------------------------------------------------------------------------------ register and report

/** One line of the admission register (Form III). */
export interface MtpRegisterRow {
  id: string;
  serialNumber: string;
  admittedOn: string | null;
  patientName: string;
  uhid: string;
  guardianName: string | null;
  ageYears: number;
  religion: string | null;
  address: string;
  weeksOfGestation: number;
  grounds: MtpGround[];
  category: MtpRule3BCategory | null;
  terminatedOn: string | null;
  method: MtpMethod | null;
  dischargedOn: string | null;
  outcome: string | null;
  opinionBy: string;
  performedBy: string | null;
  remarks: string | null;
}

export interface MtpRegister {
  year: number;
  month: number;
  approvalNumber: string;
  rows: MtpRegisterRow[];
  /** Cases certified in the month whose termination is not recorded yet. */
  awaitingTermination: number;
}

export interface MtpCount {
  label: string;
  count: number;
}

/** The month's report to the Chief Medical Officer of the district (Form II). */
export interface MtpMonthlyReport {
  year: number;
  month: number;
  approvalNumber: string;
  total: number;
  byGestation: MtpCount[];
  byReligion: MtpCount[];
  byGround: MtpCount[];
  byMethod: MtpCount[];
  byAge: MtpCount[];
}

// ------------------------------------------------------------------------------------------------ queries

export interface MtpCaseFilters {
  status?: MtpCaseStatus;
  patientId?: string;
}

export const mtpKeys = {
  all: ['mtp'] as const,
  cases: ['mtp', 'cases'] as const,
  case: (id: string) => ['mtp', 'cases', 'detail', id] as const,
  register: (year: number, month: number) => ['mtp', 'register', year, month] as const,
  monthlyReport: (year: number, month: number) => ['mtp', 'monthly-report', year, month] as const,
};

const cases = '/api/v1/mtp/cases';
const mtpCase = (id: string) => `${cases}/${id}`;

export const mtpQueries = {
  cases: (filters: MtpCaseFilters) =>
    queryOptions({
      queryKey: [...mtpKeys.cases, 'list', filters],
      queryFn: ({ signal }) => api.get<MtpCaseListItem[]>(cases, { query: { ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  case: (id: string) =>
    queryOptions({
      queryKey: mtpKeys.case(id),
      queryFn: ({ signal }) => api.get<MtpCaseDto>(mtpCase(id), { signal }),
    }),
  register: (year: number, month: number) =>
    queryOptions({
      queryKey: mtpKeys.register(year, month),
      queryFn: ({ signal }) => api.get<MtpRegister>('/api/v1/mtp/register', { query: { year, month }, signal }),
      placeholderData: keepPreviousData,
    }),
  monthlyReport: (year: number, month: number) =>
    queryOptions({
      queryKey: mtpKeys.monthlyReport(year, month),
      queryFn: ({ signal }) =>
        api.get<MtpMonthlyReport>('/api/v1/mtp/monthly-report', { query: { year, month }, signal }),
      placeholderData: keepPreviousData,
    }),
};

// ------------------------------------------------------------------------------------------------ commands

export const mtpApi = {
  open: (request: SaveMtpCaseRequest) => api.post<MtpCaseDto>(cases, request),
  /** Corrects the consent and the opinion before the termination; `request.version` is the case as read (REL-04). */
  correct: (id: string, request: SaveMtpCaseRequest) => api.put<MtpCaseDto>(mtpCase(id), request),
  terminate: (id: string, request: RecordMtpTerminationRequest) =>
    api.post<MtpCaseDto>(`${mtpCase(id)}/termination`, request),
  cancel: (id: string, reason: string, version: number) =>
    api.post<MtpCaseDto>(`${mtpCase(id)}/cancel`, { reason, version }),
  /** The month's admission register (Form III) as CSV (the download is audited). */
  exportRegister: (year: number, month: number) => api.blob('/api/v1/mtp/register/export', { query: { year, month } }),
};

/** A case's Form C and Form I as the server prints them and makes their PDF (see `@/lib/documents`). */
export const mtpCaseDocument = (id: string) => mtpCase(id);

export const MTP_PERMISSIONS = {
  /** Open the cases (MTP Act s.5A: only staff given this, or `certify`). */
  read: 'mtp.read',
  /** Open, correct, terminate and cancel a case (the doctors who form the opinion and perform it). */
  certify: 'mtp.certify',
  /** The month's admission register, its export and the report to the Chief Medical Officer. */
  register: 'mtp.register',
} as const;

/** Who may open the cases (server `MtpEndpoints.CaseReaders`). */
export const MTP_CASE_READERS = [MTP_PERMISSIONS.read, MTP_PERMISSIONS.certify] as const;
