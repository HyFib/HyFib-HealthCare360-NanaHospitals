import { saveFile, type PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DiagnosisType } from '@/modules/emr/api';

// ---------------------------------------------------------------------------------------------------- ICD catalogue

export type CodeSource = 'Seed' | 'Online' | 'Manual' | 'Import';

/** ICD-10's dagger (†, the underlying disease) and asterisk (*, how it shows in an organ) codes. */
export type IcdUsage = 'Dagger' | 'Asterisk';

export interface IcdCodeDto {
  id: string;
  code: string;
  title: string;
  release: string;
  chapter: string | null;
  block: string | null;
  blockTitle: string | null;
  isBillable: boolean;
  usage: IcdUsage | null;
  keywords: string | null;
  /** The WHO file's inclusion terms, joined with "; ". */
  includedTerms: string | null;
  source: CodeSource;
  isActive: boolean;
  version: number;
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
  version: number;
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
  /** The code as shown, when changing one: a change saved meanwhile is not overwritten (REL-04). */
  version?: number;
}

/**
 * Adds or changes a code of the procedure catalogue (`SaveProcedureCodeRequest`). `system` is ICD-10-PCS when empty;
 * the code is saved in capitals, and a code already in the catalogue for the same system is refused (`procedure.exists`).
 */
export interface SaveProcedureCodeRequest {
  code: string;
  title: string;
  system: string | null;
  category: string | null;
  keywords: string | null;
  isActive: boolean;
  /** The code as shown, when changing one: a change saved meanwhile is not overwritten (REL-04). */
  version?: number;
}

/**
 * Opens a chart by hand (`CreateMedicalRecordRequest`): for an encounter or an admission of the patient, or with
 * `paper` (and no encounter, admission or visit) for a file that exists only on paper.
 */
export interface CreateMedicalRecordRequest {
  patientId: string;
  encounterId: string | null;
  admissionId: string | null;
  visitId: string | null;
  assignedPractitionerId: string | null;
  dueDate: string | null;
  isMlc: boolean;
  paper?: PaperChart;
}

/** A paper file: the number on it, the episode's days (discharge only for a stay) and what it holds. */
export interface PaperChart {
  fileNumber: string;
  isInpatient: boolean;
  episodeFrom: string;
  episodeTo: string | null;
  note: string | null;
}

export interface IcdChapter {
  number: string;
  title: string;
  range: string;
  blocks: { range: string; title: string }[];
}

export const ICD10_RELEASE = 'ICD-10 2019';
export const ICD11_RELEASE = 'ICD-11 MMS';
export const ICD10PCS_RELEASE = 'ICD-10-PCS';

export type CatalogueKind = 'Icd10' | 'Icd10Pcs';

/** What an import of a catalogue file did, or would do (`dryRun`). */
export interface CatalogueImportResult {
  id: string | null;
  catalogue: CatalogueKind;
  release: string;
  fileName: string;
  sha256: string;
  fileSizeBytes: number;
  sourceTitle: string | null;
  sourceVersion: string | null;
  codesInFile: number;
  added: number;
  updated: number;
  unchanged: number;
  reinstated: number;
  withdrawn: number;
  /** Codes MRD added or changed by hand, or saved from the WHO online search: an import never changes them. */
  leftAlone: number;
  chapters: number;
  blocks: number;
  dryRun: boolean;
  at: string;
  importedBy: string;
  channel: 'Upload' | 'Console';
  durationMs: number;
  warnings: string[];
}

export interface CatalogueSummary {
  release: string;
  activeCodes: number;
  codableCodes: number;
  withdrawnCodes: number;
  switchedOffCodes: number;
  fromFile: number;
  fromStarterList: number;
  addedByHand: number;
  savedFromOnline: number;
  /** ICD-10 only: true once the chapters and blocks come from an imported WHO file. */
  classificationFromFile: boolean | null;
  lastImport: CatalogueImportResult | null;
}

export interface CatalogueStatus {
  catalogues: CatalogueSummary[];
  recentImports: CatalogueImportResult[];
}

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
  /** A chart of a file that exists only on paper; null for a chart of the record. */
  paper: PaperChart | null;
}

export interface AddDeficiencyRequest {
  type: DeficiencyType;
  description: string;
  assignedPractitionerId: string | null;
  dueDate: string | null;
}

export type RecordRequestStatus = 'Requested' | 'Issued' | 'Returned' | 'Overdue' | 'Cancelled';
export type RecordRequestPurpose =
  'PatientCare' | 'Legal' | 'Insurance' | 'Research' | 'Audit' | 'PatientCopy' | 'Other';

export const RECORD_REQUEST_STATUSES: readonly RecordRequestStatus[] = [
  'Requested',
  'Issued',
  'Overdue',
  'Returned',
  'Cancelled',
];
export const RECORD_REQUEST_PURPOSES: readonly RecordRequestPurpose[] = [
  'PatientCare',
  'Legal',
  'Insurance',
  'Research',
  'Audit',
  'PatientCopy',
  'Other',
];
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
  /** A copy for the patient or a legal authority is issued within 72 hours (MCI Regulations 2002, reg. 1.3.2). */
  copyDueAt: string | null;
  /** Issued after its 72 hours, or still waiting past them. */
  copyLate: boolean;
  /** The files of a copy issued as documents, in the order its certificate lists them (BSA 2023, s.63). */
  files?: RecordCopyFileDto[] | null;
}

/** A document of the patient's record that a copy can hold: signed or released, as the hospital prints it. */
export interface RecordDocumentEntry {
  kind: string;
  id: string;
  title: string;
  date: string;
  visitId: string | null;
  admissionId: string | null;
}

/** A file of a copy issued: the document as it was made then, kept unchanged, with the SHA-256 its certificate states. */
export interface RecordCopyFileDto {
  id: string;
  sequence: number;
  documentKind: string;
  documentId: string;
  title: string;
  documentDate: string;
  fileName: string;
  sizeBytes: number;
  sha256: string;
  producedAt: string;
}

/** A file issued with the SHA-256 looked for: which copy, of whose record, to whom and when. */
export interface RecordCopyMatch {
  requestId: string;
  requestNumber: string;
  uhid: string | null;
  patientName: string | null;
  purpose: RecordRequestPurpose;
  requestedByName: string;
  issuedAt: string | null;
  sequence: number;
  title: string;
  fileName: string;
  sizeBytes: number;
  sha256: string;
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
  /** Copies still waiting past their 72 hours, the longest-waiting first. */
  copyOverdue?: boolean;
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
  icd: (
    search: string,
    chapter: string | null,
    billableOnly: boolean,
    limit: number,
    release: string | null = null,
    includeInactive = false,
  ) => ['mrd', 'icd', search, chapter ?? 'all', billableOnly, limit, release ?? 'any', includeInactive] as const,
  icdOnline: (search: string, release: string) => ['mrd', 'icd-online', search, release] as const,
  classification: () => ['mrd', 'icd-classification'] as const,
  catalogue: () => ['mrd', 'catalogue'] as const,
  procedures: (search: string) => ['mrd', 'procedures', search] as const,
  procedureCatalogue: (search: string, includeInactive = false) =>
    ['mrd', 'procedure-catalogue', search, includeInactive] as const,
  codings: (params: CodingListParams) => ['mrd', 'codings', params] as const,
  coding: (id: string) => ['mrd', 'coding', id] as const,
  suggestions: (id: string) => ['mrd', 'coding', id, 'suggestions'] as const,
  records: (params: RecordListParams) => ['mrd', 'records', params] as const,
  record: (id: string) => ['mrd', 'record', id] as const,
  requests: (params: RequestListParams) => ['mrd', 'requests', params] as const,
  requestDocuments: (id: string) => ['mrd', 'request-documents', id] as const,
  morbidity: (params: MorbidityParams) => ['mrd', 'morbidity', params] as const,
};

/**
 * Ranked search of the local catalogue. `release` limits it to one classification (`ICD10_RELEASE` for claims and
 * MRD coding); without it ICD-10 codes and ICD-11 codes saved from the WHO online search are both found.
 */
export function searchIcd(
  search: string,
  options: {
    chapter?: string | null;
    billableOnly?: boolean;
    limit?: number;
    release?: string | null;
    /** Also codes switched off or withdrawn by an import, for MRD to switch back on. Pickers leave it out. */
    includeInactive?: boolean;
    signal?: AbortSignal;
  } = {},
): Promise<IcdCodeDto[]> {
  return api.get<IcdCodeDto[]>('/api/v1/mrd/icd-codes', {
    query: {
      search: search || undefined,
      chapter: options.chapter ?? undefined,
      billableOnly: options.billableOnly || undefined,
      limit: options.limit ?? 20,
      release: options.release ?? undefined,
      includeInactive: options.includeInactive || undefined,
    },
    signal: options.signal,
  });
}

export function searchProcedures(search: string, signal?: AbortSignal): Promise<ProcedureCodeDto[]> {
  return api.get<ProcedureCodeDto[]>('/api/v1/mrd/procedure-codes', {
    query: { search: search || undefined, limit: 20 },
    signal,
  });
}

export const mrdQueries = {
  icd: (
    search: string,
    chapter: string | null = null,
    billableOnly = false,
    limit = 20,
    release: string | null = null,
    includeInactive = false,
  ) =>
    queryOptions({
      queryKey: mrdKeys.icd(search, chapter, billableOnly, limit, release, includeInactive),
      queryFn: ({ signal }) => searchIcd(search, { chapter, billableOnly, limit, release, includeInactive, signal }),
      staleTime: 10 * 60_000,
      placeholderData: keepPreviousData,
    }),
  /** WHO's online service: ICD-11 MMS by words, or an ICD-10 code and its subdivisions (it has no ICD-10 word search). */
  icdOnline: (search: string, release: string = ICD11_RELEASE) =>
    queryOptions({
      queryKey: mrdKeys.icdOnline(search, release),
      queryFn: ({ signal }) =>
        api.get<OnlineIcdResult[]>('/api/v1/mrd/icd-codes/online', { query: { search, release, limit: 20 }, signal }),
      staleTime: 10 * 60_000,
      retry: false,
    }),
  catalogue: () =>
    queryOptions({
      queryKey: mrdKeys.catalogue(),
      queryFn: ({ signal }) => api.get<CatalogueStatus>('/api/v1/mrd/catalogue', { signal }),
      staleTime: 60_000,
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
  /**
   * The procedure catalogue to maintain: the first 100 active codes by category, or the best 100 for `search`; with
   * `includeInactive`, switched-off codes too, so MRD can switch one back on.
   */
  procedureCatalogue: (search: string, includeInactive = false) =>
    queryOptions({
      queryKey: mrdKeys.procedureCatalogue(search, includeInactive),
      queryFn: ({ signal }) =>
        api.get<ProcedureCodeDto[]>('/api/v1/mrd/procedure-codes', {
          query: { search: search || undefined, includeInactive: includeInactive || undefined, limit: 100 },
          signal,
        }),
      staleTime: 60_000,
      placeholderData: keepPreviousData,
    }),
  codings: (params: CodingListParams) =>
    queryOptions({
      queryKey: mrdKeys.codings(params),
      queryFn: ({ signal }) =>
        api.get<PagedResult<CodingDto>>('/api/v1/mrd/codings', { query: { ...params, pageSize: 25 }, signal }),
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
          query: {
            status: params.status,
            overdue: params.overdue || undefined,
            retentionDue: params.retentionDue || undefined,
            search: params.search,
            page: params.page,
            pageSize: 25,
          },
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
      queryFn: ({ signal }) =>
        api.get<PagedResult<RecordRequestDto>>('/api/v1/mrd/record-requests', {
          query: { ...params, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  /** The patient's documents a copy of the request can hold, newest first. */
  requestDocuments: (id: string) =>
    queryOptions({
      queryKey: mrdKeys.requestDocuments(id),
      queryFn: ({ signal }) =>
        api.get<RecordDocumentEntry[]>(`/api/v1/mrd/record-requests/${id}/documents`, { signal }),
    }),
  morbidity: (params: MorbidityParams) =>
    queryOptions({
      queryKey: mrdKeys.morbidity(params),
      queryFn: ({ signal }) =>
        api.get<MorbidityReport>('/api/v1/mrd/reports/morbidity', { query: { ...params }, signal }),
      placeholderData: keepPreviousData,
    }),
};

// ---------------------------------------------------------------------------------------------------- commands

export const mrdApi = {
  saveIcd: (id: string | null, request: SaveIcdCodeRequest) =>
    id
      ? api.put<IcdCodeDto>(`/api/v1/mrd/icd-codes/${id}`, request)
      : api.post<IcdCodeDto>('/api/v1/mrd/icd-codes', request),
  saveProcedure: (id: string | null, request: SaveProcedureCodeRequest) =>
    id
      ? api.put<ProcedureCodeDto>(`/api/v1/mrd/procedure-codes/${id}`, request)
      : api.post<ProcedureCodeDto>('/api/v1/mrd/procedure-codes', request),
  /**
   * Uploads the WHO ICD-10 ClaML file or the CMS ICD-10-PCS order file. `dryRun` only reports what would change. A real
   * file takes the server tens of seconds, so the call may take up to ten minutes before it gives up.
   */
  importCatalogue: (file: File, dryRun: boolean) => {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('dryRun', dryRun ? 'true' : 'false');
    return api.post<CatalogueImportResult>('/api/v1/mrd/catalogue/imports', form, { timeoutMs: 10 * 60_000 });
  },
  createCoding: (request: CreateCodingRequest) => api.post<CodingDto>('/api/v1/mrd/codings', request),
  saveCoding: (id: string, entries: CodingLineRequest[], notes: string | null, version: number) =>
    api.put<CodingDto>(`/api/v1/mrd/codings/${id}`, { entries, notes, version }),
  verifyCoding: (id: string, version: number) => api.post<CodingDto>(`/api/v1/mrd/codings/${id}/verify`, { version }),
  createRecord: (request: CreateMedicalRecordRequest) => api.post<MedicalRecordDto>('/api/v1/mrd/records', request),
  addDeficiency: (recordId: string, request: AddDeficiencyRequest) =>
    api.post<MedicalRecordDto>(`/api/v1/mrd/records/${recordId}/deficiencies`, request),
  resolveDeficiency: (deficiencyId: string, note: string | null) =>
    api.post<MedicalRecordDto>(`/api/v1/mrd/records/deficiencies/${deficiencyId}/resolve`, { note }),
  scan: (recordId: string) => api.post<MedicalRecordDto>(`/api/v1/mrd/records/${recordId}/scan`),
  complete: (recordId: string) => api.post<MedicalRecordDto>(`/api/v1/mrd/records/${recordId}/complete`),
  archive: (recordId: string, location: string) =>
    api.post<MedicalRecordDto>(`/api/v1/mrd/records/${recordId}/archive`, { location }),
  createRequest: (request: CreateRecordRequestRequest) =>
    api.post<RecordRequestDto>('/api/v1/mrd/record-requests', request),
  /**
   * Issues the chart, or a copy. A copy's `documents` are made into PDFs now and kept as they are handed over, so this
   * waits as long as making a PDF may take.
   */
  issueRequest: (id: string, dueBackOn: string | null, documents: { kind: string; id: string }[]) =>
    api.post<RecordRequestDto>(
      `/api/v1/mrd/record-requests/${id}/issue`,
      { dueBackOn, documents },
      { timeoutMs: 120_000 },
    ),
  /** A file of a copy, byte for byte as it was issued. */
  downloadCopyFile: async (requestId: string, file: RecordCopyFileDto) => {
    const { blob } = await api.download(`/api/v1/mrd/record-requests/${requestId}/files/${file.id}`);
    saveFile(blob, file.fileName);
  },
  /** Which copy issued a file with this SHA-256, if any. The file itself never leaves this computer. */
  verifyCopy: (sha256: string) => api.post<RecordCopyMatch[]>('/api/v1/mrd/record-copies/verify', { sha256 }),
  returnRequest: (id: string) => api.post<RecordRequestDto>(`/api/v1/mrd/record-requests/${id}/return`),
  cancelRequest: (id: string, reason: string) =>
    api.post<RecordRequestDto>(`/api/v1/mrd/record-requests/${id}/cancel`, { reason }),
};

/** The certificate of a copy under BSA 2023 s.63(4): `<path>/document` to print, `<path>/pdf` to download. */
export const copyCertificatePath = (requestId: string) => `/api/v1/mrd/record-requests/${requestId}/certificate`;
