import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DiagnosisType } from '@/modules/emr/api';

// ---------------------------------------------------------------------------------------------------- ICD catalogue

export type CodeSource = 'Seed' | 'Online' | 'Manual';

export interface IcdCodeDto {
  id: string;
  code: string;
  title: string;
  release: string;
  chapter: string | null;
  block: string | null;
  blockTitle: string | null;
  isBillable: boolean;
  keywords: string | null;
  source: CodeSource;
  isActive: boolean;
}

export interface ProcedureCodeDto {
  id: string;
  code: string;
  title: string;
  system: string;
  category: string | null;
  keywords: string | null;
  source: CodeSource;
  isActive: boolean;
}

export interface OnlineIcdResult {
  code: string;
  title: string;
  release: string;
  chapter: string | null;
  isLeaf: boolean;
  isCachedLocally: boolean;
}

export interface SaveIcdCodeRequest {
  code: string;
  title: string;
  release: string | null;
  isBillable: boolean;
  keywords: string | null;
  fromOnlineLookup: boolean;
  isActive: boolean;
}

export interface IcdChapter {
  number: string;
  title: string;
  range: string;
  blocks: { range: string; title: string }[];
}

export const ICD10_RELEASE = 'ICD-10 2019';
export const ICD11_RELEASE = 'ICD-11 MMS';

// ---------------------------------------------------------------------------------------------------- coding

export type CodingStatus = 'Pending' | 'Coded' | 'Verified';
export type CodingEntryKind = 'Diagnosis' | 'Procedure';

export interface CodingEntryDto {
  id: string;
  kind: CodingEntryKind;
  code: string;
  title: string;
  isPrimary: boolean;
  sequence: number;
}

export interface CodingDto {
  id: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  encounterId: string | null;
  admissionId: string | null;
  visitId: string | null;
  episodeDate: string | null;
  status: CodingStatus;
  coderUserId: string | null;
  codedAt: string | null;
  verifiedByUserId: string | null;
  verifiedAt: string | null;
  notes: string | null;
  entries: CodingEntryDto[];
  version: number;
}

export interface CodingSuggestion {
  code: string | null;
  title: string;
  type: DiagnosisType;
  isPrimary: boolean;
  source: string;
}

export interface CreateCodingRequest {
  patientId: string;
  encounterId: string | null;
  admissionId: string | null;
  visitId: string | null;
  episodeDate: string | null;
  notes: string | null;
}

export interface CodingLineRequest {
  kind: CodingEntryKind;
  code: string;
  isPrimary: boolean;
}

export type MorbidityGroupBy = 'code' | 'block' | 'chapter';
export type MorbiditySource = 'clinical' | 'coded';

export interface MorbidityRow {
  key: string;
  title: string;
  chapter: string | null;
  episodes: number;
  patients: number;
  male: number;
  female: number;
  otherOrUnknownGender: number;
  age0To14: number;
  age15To44: number;
  age45To64: number;
  age65Plus: number;
  ageUnknown: number;
}

export interface MorbidityReport {
  from: string;
  to: string;
  groupBy: string;
  source: string;
  totalEpisodes: number;
  rows: MorbidityRow[];
}

// ---------------------------------------------------------------------------------------------------- charts and record requests

export type MedicalRecordStatus = 'Incomplete' | 'Complete' | 'Deficient' | 'Archived';

export type DeficiencyType =
  | 'MissingDischargeSummary'
  | 'UnsignedEncounter'
  | 'UnsignedNote'
  | 'MissingDiagnosis'
  | 'MissingConsent'
  | 'MissingSignature'
  | 'PendingCoding'
  | 'Other';

export const DEFICIENCY_TYPES: readonly DeficiencyType[] = [
  'MissingDischargeSummary',
  'UnsignedEncounter',
  'UnsignedNote',
  'MissingDiagnosis',
  'MissingConsent',
  'MissingSignature',
  'PendingCoding',
  'Other',
];

export const deficiencyLabel: Record<DeficiencyType, string> = {
  MissingDischargeSummary: 'Discharge summary missing or unsigned',
  UnsignedEncounter: 'Encounter not signed',
  UnsignedNote: 'Note not signed',
  MissingDiagnosis: 'Diagnosis missing',
  MissingConsent: 'Consent missing',
  MissingSignature: 'Signature missing',
  PendingCoding: 'Coding pending',
  Other: 'Other',
};

export interface ChartDeficiencyDto {
  id: string;
  type: DeficiencyType;
  description: string;
  reference: string | null;
  assignedPractitionerId: string | null;
  assignedPractitionerName: string | null;
  dueDate: string | null;
  raisedAt: string;
  isSystemDetected: boolean;
  resolvedAt: string | null;
  resolutionNote: string | null;
}

export interface MedicalRecordDto {
  id: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  encounterId: string | null;
  admissionId: string | null;
  visitId: string | null;
  admittedAt: string | null;
  dischargedAt: string | null;
  status: MedicalRecordStatus;
  assignedPractitionerId: string | null;
  dueDate: string | null;
  isOverdue: boolean;
  isMlc: boolean;
  completedAt: string | null;
  archivedAt: string | null;
  archiveLocation: string | null;
  retainUntil: string | null;
  openDeficiencies: number;
  deficiencies: ChartDeficiencyDto[];
  version: number;
}

export interface AddDeficiencyRequest {
  type: DeficiencyType;
  description: string;
  assignedPractitionerId: string | null;
  dueDate: string | null;
}

export type RecordRequestStatus = 'Requested' | 'Issued' | 'Returned' | 'Overdue' | 'Cancelled';
export type RecordRequestPurpose = 'PatientCare' | 'Legal' | 'Insurance' | 'Research' | 'Audit' | 'PatientCopy' | 'Other';

export const RECORD_REQUEST_STATUSES: readonly RecordRequestStatus[] = ['Requested', 'Issued', 'Overdue', 'Returned', 'Cancelled'];
export const RECORD_REQUEST_PURPOSES: readonly RecordRequestPurpose[] = ['PatientCare', 'Legal', 'Insurance', 'Research', 'Audit', 'PatientCopy', 'Other'];
export const purposeLabel: Record<RecordRequestPurpose, string> = {
  PatientCare: 'Patient care',
  Legal: 'Legal / court',
  Insurance: 'Insurance claim',
  Research: 'Research',
  Audit: 'Audit',
  PatientCopy: 'Copy for the patient',
  Other: 'Other',
};

export interface RecordRequestDto {
  id: string;
  requestNumber: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  medicalRecordId: string | null;
  requestedByName: string;
  requestingDepartment: string | null;
  purpose: RecordRequestPurpose;
  details: string | null;
  isPatientRequest: boolean;
  status: RecordRequestStatus;
  requestedAt: string;
  issuedAt: string | null;
  dueBackOn: string | null;
  returnedAt: string | null;
  cancelReason: string | null;
}

export interface CreateRecordRequestRequest {
  patientId: string;
  medicalRecordId: string | null;
  requestedByName: string;
  requestingDepartment: string | null;
  purpose: RecordRequestPurpose;
  details: string | null;
}

// ---------------------------------------------------------------------------------------------------- queries

export interface CodingListParams {
  status?: CodingStatus;
  from?: string;
  to?: string;
  page?: number;
}

export interface RecordListParams {
  status?: MedicalRecordStatus;
  overdue?: boolean;
  retentionDue?: boolean;
  search?: string;
  page?: number;
}

export interface RequestListParams {
  status?: RecordRequestStatus;
  page?: number;
}

export interface MorbidityParams {
  from: string;
  to: string;
  groupBy: MorbidityGroupBy;
  source: MorbiditySource;
}

export const mrdKeys = {
  all: ['mrd'] as const,
  icd: (search: string, chapter: string | null, billableOnly: boolean, limit: number) => ['mrd', 'icd', search, chapter ?? 'all', billableOnly, limit] as const,
  icdOnline: (search: string) => ['mrd', 'icd-online', search] as const,
  classification: () => ['mrd', 'icd-classification'] as const,
  procedures: (search: string) => ['mrd', 'procedures', search] as const,
  codings: (params: CodingListParams) => ['mrd', 'codings', params] as const,
  coding: (id: string) => ['mrd', 'coding', id] as const,
  suggestions: (id: string) => ['mrd', 'coding', id, 'suggestions'] as const,
  records: (params: RecordListParams) => ['mrd', 'records', params] as const,
  record: (id: string) => ['mrd', 'record', id] as const,
  requests: (params: RequestListParams) => ['mrd', 'requests', params] as const,
  morbidity: (params: MorbidityParams) => ['mrd', 'morbidity', params] as const,
};

export function searchIcd(search: string, options: { chapter?: string | null; billableOnly?: boolean; limit?: number; signal?: AbortSignal } = {}): Promise<IcdCodeDto[]> {
  return api.get<IcdCodeDto[]>('/api/v1/mrd/icd-codes', {
    query: { search: search || undefined, chapter: options.chapter ?? undefined, billableOnly: options.billableOnly || undefined, limit: options.limit ?? 20 },
    signal: options.signal,
  });
}

export function searchProcedures(search: string, signal?: AbortSignal): Promise<ProcedureCodeDto[]> {
  return api.get<ProcedureCodeDto[]>('/api/v1/mrd/procedure-codes', { query: { search: search || undefined, limit: 20 }, signal });
}

export const mrdQueries = {
  icd: (search: string, chapter: string | null = null, billableOnly = false, limit = 20) =>
    queryOptions({
      queryKey: mrdKeys.icd(search, chapter, billableOnly, limit),
      queryFn: ({ signal }) => searchIcd(search, { chapter, billableOnly, limit, signal }),
      staleTime: 10 * 60_000,
      placeholderData: keepPreviousData,
    }),
  icdOnline: (search: string) =>
    queryOptions({
      queryKey: mrdKeys.icdOnline(search),
      queryFn: ({ signal }) => api.get<OnlineIcdResult[]>('/api/v1/mrd/icd-codes/online', { query: { search, limit: 20 }, signal }),
      staleTime: 10 * 60_000,
      retry: false,
    }),
  classification: () =>
    queryOptions({
      queryKey: mrdKeys.classification(),
      queryFn: ({ signal }) => api.get<IcdChapter[]>('/api/v1/mrd/icd-classification', { signal }),
      staleTime: Infinity,
    }),
  procedures: (search: string) =>
    queryOptions({
      queryKey: mrdKeys.procedures(search),
      queryFn: ({ signal }) => searchProcedures(search, signal),
      staleTime: 10 * 60_000,
    }),
  codings: (params: CodingListParams) =>
    queryOptions({
      queryKey: mrdKeys.codings(params),
      queryFn: ({ signal }) => api.get<PagedResult<CodingDto>>('/api/v1/mrd/codings', { query: { ...params, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  coding: (id: string) =>
    queryOptions({
      queryKey: mrdKeys.coding(id),
      queryFn: ({ signal }) => api.get<CodingDto>(`/api/v1/mrd/codings/${id}`, { signal }),
    }),
  suggestions: (id: string) =>
    queryOptions({
      queryKey: mrdKeys.suggestions(id),
      queryFn: ({ signal }) => api.get<CodingSuggestion[]>(`/api/v1/mrd/codings/${id}/suggestions`, { signal }),
    }),
  records: (params: RecordListParams) =>
    queryOptions({
      queryKey: mrdKeys.records(params),
      queryFn: ({ signal }) =>
        api.get<PagedResult<MedicalRecordDto>>('/api/v1/mrd/records', {
          query: { status: params.status, overdue: params.overdue || undefined, retentionDue: params.retentionDue || undefined, search: params.search, page: params.page, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  record: (id: string) =>
    queryOptions({
      queryKey: mrdKeys.record(id),
      queryFn: ({ signal }) => api.get<MedicalRecordDto>(`/api/v1/mrd/records/${id}`, { signal }),
    }),
  requests: (params: RequestListParams) =>
    queryOptions({
      queryKey: mrdKeys.requests(params),
      queryFn: ({ signal }) => api.get<PagedResult<RecordRequestDto>>('/api/v1/mrd/record-requests', { query: { ...params, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  morbidity: (params: MorbidityParams) =>
    queryOptions({
      queryKey: mrdKeys.morbidity(params),
      queryFn: ({ signal }) => api.get<MorbidityReport>('/api/v1/mrd/reports/morbidity', { query: { ...params }, signal }),
      placeholderData: keepPreviousData,
    }),
};

// ---------------------------------------------------------------------------------------------------- commands

export const mrdApi = {
  saveIcd: (id: string | null, request: SaveIcdCodeRequest) =>
    id ? api.put<IcdCodeDto>(`/api/v1/mrd/icd-codes/${id}`, request) : api.post<IcdCodeDto>('/api/v1/mrd/icd-codes', request),
  createCoding: (request: CreateCodingRequest) => api.post<CodingDto>('/api/v1/mrd/codings', request),
  saveCoding: (id: string, entries: CodingLineRequest[], notes: string | null, version: number) =>
    api.put<CodingDto>(`/api/v1/mrd/codings/${id}`, { entries, notes, version }),
  verifyCoding: (id: string, version: number) => api.post<CodingDto>(`/api/v1/mrd/codings/${id}/verify`, { version }),
  addDeficiency: (recordId: string, request: AddDeficiencyRequest) => api.post<MedicalRecordDto>(`/api/v1/mrd/records/${recordId}/deficiencies`, request),
  resolveDeficiency: (deficiencyId: string, note: string | null) =>
    api.post<MedicalRecordDto>(`/api/v1/mrd/records/deficiencies/${deficiencyId}/resolve`, { note }),
  scan: (recordId: string) => api.post<MedicalRecordDto>(`/api/v1/mrd/records/${recordId}/scan`),
  complete: (recordId: string) => api.post<MedicalRecordDto>(`/api/v1/mrd/records/${recordId}/complete`),
  archive: (recordId: string, location: string) => api.post<MedicalRecordDto>(`/api/v1/mrd/records/${recordId}/archive`, { location }),
  createRequest: (request: CreateRecordRequestRequest) => api.post<RecordRequestDto>('/api/v1/mrd/record-requests', request),
  issueRequest: (id: string, dueBackOn: string | null) => api.post<RecordRequestDto>(`/api/v1/mrd/record-requests/${id}/issue`, { dueBackOn }),
  returnRequest: (id: string) => api.post<RecordRequestDto>(`/api/v1/mrd/record-requests/${id}/return`),
  cancelRequest: (id: string, reason: string) => api.post<RecordRequestDto>(`/api/v1/mrd/record-requests/${id}/cancel`, { reason }),
};
