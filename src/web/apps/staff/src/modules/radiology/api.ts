import { isApiError, type Gender, type PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  CriticalCallDetails,
  DiagnosticReportStatus,
  OrderPriority,
  PatientClass,
  ReportStudyRef,
  TatSummary,
} from '@/modules/lab/common/types';

// ------------------------------------------------------------------------------------------------ enums (as strings)

export const MODALITIES = ['XR', 'CT', 'MR', 'US', 'MG', 'RF', 'DX'] as const;
export type Modality = (typeof MODALITIES)[number];

export const STUDY_STATUSES = [
  'Ordered',
  'Scheduled',
  'Arrived',
  'InProgress',
  'Acquired',
  'Reported',
  'Verified',
  'Released',
  'Cancelled',
] as const;
export type ImagingStudyStatus = (typeof STUDY_STATUSES)[number];

export const REPEAT_REASONS = [
  'Positioning',
  'Exposure',
  'PatientMotion',
  'Artefact',
  'EquipmentFault',
  'WrongProtocol',
  'PatientRelated',
  'Other',
] as const;
export type ImageRepeatReason = (typeof REPEAT_REASONS)[number];

export type AiFindingStatus = 'Preliminary' | 'Acknowledged' | 'Dismissed';

// ------------------------------------------------------------------------------------------------ studies

export interface ImagingStudyListItem {
  id: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  patientClass: PatientClass;
  studyName: string;
  modality: Modality;
  bodyPart: string | null;
  priority: OrderPriority;
  status: ImagingStudyStatus;
  orderedAt: string;
  scheduledAt: string | null;
  room: string | null;
  hasUrgentAiFinding: boolean;
  reportStatus: DiagnosticReportStatus | null;
  isCriticalFinding: boolean;
}

export interface ImagingRepeatDto {
  id: string;
  reason: ImageRepeatReason;
  note: string | null;
  recordedAt: string;
}

export interface ImagingStudyDetail {
  id: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  visitId: string | null;
  admissionId: string | null;
  patientClass: PatientClass;
  sourceOrderNumber: string | null;
  serviceCode: string;
  studyName: string;
  modality: Modality;
  bodyPart: string | null;
  priority: OrderPriority;
  orderingPractitionerId: string | null;
  orderingDoctor: string | null;
  clinicalNotes: string | null;
  orderedAt: string;
  status: ImagingStudyStatus;
  scheduledAt: string | null;
  room: string | null;
  stationAeTitle: string | null;
  arrivedAt: string | null;
  startedAt: string | null;
  acquiredAt: string | null;
  technician: string | null;
  contrastUsed: boolean;
  contrastAgent: string | null;
  contrastVolumeMl: number | null;
  doseNote: string | null;
  technicianNotes: string | null;
  studyInstanceUid: string;
  /** False when the accession number is longer than DICOM's 16 characters (see `worklistProcedureId`). */
  accessionFitsDicom: boolean;
  /** The procedure id the modality worklist sends: the accession number, or an id of its own when that does not fit. */
  worklistProcedureId: string;
  pacsLinkedAt: string | null;
  seriesCount: number | null;
  instanceCount: number | null;
  reportedAt: string | null;
  verifiedAt: string | null;
  releasedAt: string | null;
  cancelReason: string | null;
  chargeError: string | null;
  packageBookingId: string | null;
  quantity: number;
  patientPreparation: string | null;
  repeats: ImagingRepeatDto[];
  version: number;
}

export interface ScheduleStudyRequest {
  scheduledAt: string;
  room: string | null;
  stationAeTitle: string | null;
}

export interface AcquireStudyRequest {
  contrastUsed: boolean;
  contrastAgent: string | null;
  contrastVolumeMl: number | null;
  doseNote: string | null;
  notes: string | null;
}

export interface ViewerLink {
  studyId: string;
  url: string;
  studyInstanceUid: string;
}

export interface PacsReconciliationSummary {
  checked: number;
  linked: number;
  messages: string[];
}

/** A series of a study's pictures in PACS (ImageSeriesDto). */
export interface ImageSeries {
  seriesInstanceUid: string;
  seriesNumber: number | null;
  modality: string | null;
  description: string | null;
  bodyPart: string | null;
  imageCount: number | null;
}

/** StudyImagesDto: the study's picture series in PACS. */
export interface StudyImages {
  studyId: string;
  accessionNumber: string;
  studyInstanceUid: string;
  series: ImageSeries[];
}

/** One object of a series; `frames` is more than 1 for a cine loop or multi-frame image. */
export interface ImageInstance {
  sopInstanceUid: string;
  instanceNumber: number | null;
  frames: number;
}

export interface ImageSeriesDetail {
  studyId: string;
  series: ImageSeries;
  instances: ImageInstance[];
}

// ------------------------------------------------------------------------------------------------ reports

export interface RadiologyAddendumDto {
  id: string;
  text: string;
  reason: string | null;
  isCriticalFinding: boolean;
  addedAt: string;
  addedBy: string | null;
}

export interface RadiologyReportDto {
  id: string;
  imagingStudyId: string;
  templateId: string | null;
  technique: string | null;
  clinicalHistory: string | null;
  comparison: string | null;
  findings: string;
  impression: string;
  isCriticalFinding: boolean;
  criticalFindingNote: string | null;
  status: DiagnosticReportStatus;
  reportedBy: string | null;
  reportedAt: string | null;
  verifiedBy: string | null;
  verifiedAt: string | null;
  releasedAt: string | null;
  addenda: RadiologyAddendumDto[];
  version: number;
}

export interface SaveRadiologyReportRequest {
  templateId: string | null;
  technique: string | null;
  clinicalHistory: string | null;
  comparison: string | null;
  findings: string;
  impression: string;
  isCriticalFinding: boolean;
  criticalFindingNote: string | null;
  version: number | null;
}

export interface AddAddendumRequest {
  text: string;
  reason: string | null;
  isCriticalFinding: boolean;
  criticalCall: CriticalCallDetails | null;
}

export interface ReportTemplateDto {
  id: string;
  code: string;
  name: string;
  modality: Modality;
  bodyPart: string | null;
  technique: string | null;
  findings: string;
  impression: string;
  isActive: boolean;
  version: number;
}

export type SaveReportTemplateRequest = Omit<ReportTemplateDto, 'id' | 'version'> & { version: number | null };

export interface ImagingProcedureDto {
  id: string;
  code: string;
  name: string;
  modality: Modality;
  bodyPart: string;
  serviceItemId: string | null;
  durationMinutes: number;
  requiresContrast: boolean;
  patientPreparation: string | null;
  isActive: boolean;
  version: number;
}

export type SaveImagingProcedureRequest = Omit<ImagingProcedureDto, 'id' | 'version'> & { version: number | null };

export interface AiFindingDto {
  id: string;
  engine: string;
  engineVersion: string | null;
  finding: string;
  confidence: number | null;
  isUrgent: boolean;
  receivedAt: string;
  status: AiFindingStatus;
  reviewedBy: string | null;
  reviewedAt: string | null;
  reviewNote: string | null;
}

export interface RepeatReasonStat {
  reason: ImageRepeatReason;
  count: number;
}

export interface RadiologyTatRow {
  modality: Modality;
  studies: number;
  reported: number;
  orderToAcquisition: TatSummary;
  acquisitionToReport: TatSummary;
  orderToReport: TatSummary;
  repeats: number;
  repeatRatePercent: number | null;
}

export interface RadiologyTatReport {
  from: string;
  to: string;
  modalities: RadiologyTatRow[];
  repeatReasons: RepeatReasonStat[];
}

// ------------------------------------------------------------------------------------------------ queries

export interface StudyFilters {
  from?: string;
  to?: string;
  status?: ImagingStudyStatus;
  modality?: Modality;
  patientId?: string;
  search?: string;
  page?: number;
}

const PAGE = 25;

export const radiologyKeys = {
  all: ['radiology'] as const,
  studies: ['radiology', 'studies'] as const,
  study: (id: string) => ['radiology', 'studies', 'detail', id] as const,
  report: (studyId: string) => ['radiology', 'report', studyId] as const,
  reportStudy: (reportId: string) => ['radiology', 'report-study', reportId] as const,
  masters: ['radiology', 'masters'] as const,
  images: (studyId: string) => ['radiology', 'images', studyId] as const,
};

const images = (studyId: string) => `/api/v1/radiology/studies/${studyId}/images`;
const seriesPath = (studyId: string, seriesUid: string) => `${images(studyId)}/series/${encodeURIComponent(seriesUid)}`;

/** Images stay in memory a short while only: they are patient data, and the server forbids storing them. */
const IMAGE_CACHE_MS = 2 * 60_000;

export const radiologyQueries = {
  studies: (filters: StudyFilters) =>
    queryOptions({
      queryKey: ['radiology', 'studies', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ImagingStudyListItem>>('/api/v1/radiology/studies', {
          query: { ...filters, pageSize: PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  study: (id: string) =>
    queryOptions({
      queryKey: radiologyKeys.study(id),
      queryFn: ({ signal }) => api.get<ImagingStudyDetail>(`/api/v1/radiology/studies/${id}`, { signal }),
    }),
  reportingWorklist: (modality?: Modality, page?: number) =>
    queryOptions({
      queryKey: ['radiology', 'studies', 'reporting', modality ?? 'all', page ?? 1],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ImagingStudyListItem>>('/api/v1/radiology/reporting-worklist', {
          query: { modality, page, pageSize: PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  /** The report of a study, or null when none has been started (the server answers 404). */
  report: (studyId: string) =>
    queryOptions({
      queryKey: radiologyKeys.report(studyId),
      queryFn: async ({ signal }) => {
        try {
          return await api.get<RadiologyReportDto>(`/api/v1/radiology/studies/${studyId}/report`, { signal });
        } catch (error) {
          if (isApiError(error) && error.status === 404) {
            return null;
          }
          throw error;
        }
      },
      refetchOnWindowFocus: false,
    }),
  /** The study of a report, for screens that hold only the report id (the doctor's results); 404 when not visible here. */
  reportStudy: (reportId: string) =>
    queryOptions({
      queryKey: radiologyKeys.reportStudy(reportId),
      queryFn: ({ signal }) => api.get<ReportStudyRef>(`/api/v1/radiology/reports/${reportId}`, { signal }),
      staleTime: 5 * 60_000,
    }),
  aiFindings: (studyId: string) =>
    queryOptions({
      queryKey: ['radiology', 'ai-findings', studyId],
      queryFn: ({ signal }) => api.get<AiFindingDto[]>(`/api/v1/radiology/studies/${studyId}/ai-findings`, { signal }),
    }),
  templates: (modality?: Modality, bodyPart?: string | null, includeInactive = false) =>
    queryOptions({
      queryKey: ['radiology', 'masters', 'templates', modality ?? 'all', bodyPart ?? '', includeInactive],
      queryFn: ({ signal }) =>
        api.get<ReportTemplateDto[]>('/api/v1/radiology/templates', {
          query: { modality, bodyPart: bodyPart ?? undefined, includeInactive },
          signal,
        }),
      staleTime: 10 * 60_000,
    }),
  procedures: (modality?: Modality, includeInactive = false) =>
    queryOptions({
      queryKey: ['radiology', 'masters', 'procedures', modality ?? 'all', includeInactive],
      queryFn: ({ signal }) =>
        api.get<ImagingProcedureDto[]>('/api/v1/radiology/procedures', {
          query: { modality, includeInactive },
          signal,
        }),
      staleTime: 10 * 60_000,
    }),
  /** The study's picture series in PACS (422 `pacs.not_linked` until the study is linked). */
  images: (studyId: string) =>
    queryOptions({
      queryKey: radiologyKeys.images(studyId),
      queryFn: ({ signal }) => api.get<StudyImages>(images(studyId), { signal }),
      staleTime: 60_000,
      retry: false,
    }),
  seriesImages: (studyId: string, seriesUid: string) =>
    queryOptions({
      queryKey: [...radiologyKeys.images(studyId), 'series', seriesUid],
      queryFn: ({ signal }) => api.get<ImageSeriesDetail>(seriesPath(studyId, seriesUid), { signal }),
      staleTime: 60_000,
      retry: false,
    }),
  thumbnail: (studyId: string, seriesUid: string) =>
    queryOptions({
      queryKey: [...radiologyKeys.images(studyId), 'thumbnail', seriesUid],
      queryFn: ({ signal }) => api.blob(`${seriesPath(studyId, seriesUid)}/thumbnail`, { signal }),
      staleTime: IMAGE_CACHE_MS,
      gcTime: IMAGE_CACHE_MS,
      retry: false,
    }),
  /** One picture (a frame of a multi-frame object when `frame` is set), rendered by PACS within `size` pixels. */
  image: (studyId: string, seriesUid: string, instanceUid: string, frame: number | null, size: number) =>
    queryOptions({
      queryKey: [...radiologyKeys.images(studyId), 'image', seriesUid, instanceUid, frame ?? 0, size],
      queryFn: ({ signal }) =>
        api.blob(`${seriesPath(studyId, seriesUid)}/instances/${encodeURIComponent(instanceUid)}/rendered`, {
          query: { frame: frame ?? undefined, size },
          signal,
        }),
      staleTime: IMAGE_CACHE_MS,
      gcTime: IMAGE_CACHE_MS,
      retry: false,
    }),
  tat: (from?: string, to?: string, modality?: Modality) =>
    queryOptions({
      queryKey: ['radiology', 'tat', from ?? 'today', to ?? 'today', modality ?? 'all'],
      queryFn: ({ signal }) =>
        api.get<RadiologyTatReport>('/api/v1/radiology/tat', { query: { from, to, modality }, signal }),
    }),
};

// ------------------------------------------------------------------------------------------------ commands

const study = (id: string) => `/api/v1/radiology/studies/${id}`;

export const radiologyApi = {
  schedule: (id: string, request: ScheduleStudyRequest) =>
    api.post<ImagingStudyDetail>(`${study(id)}/schedule`, request),
  arrive: (id: string) => api.post<ImagingStudyDetail>(`${study(id)}/arrive`),
  start: (id: string) => api.post<ImagingStudyDetail>(`${study(id)}/start`),
  acquire: (id: string, request: AcquireStudyRequest) => api.post<ImagingStudyDetail>(`${study(id)}/acquire`, request),
  recordRepeat: (id: string, reason: ImageRepeatReason, note: string | null) =>
    api.post<ImagingStudyDetail>(`${study(id)}/repeats`, { reason, note }),
  cancel: (id: string, reason: string) => api.post<ImagingStudyDetail>(`${study(id)}/cancel`, { reason }),
  reconcile: (id: string) => api.post<ImagingStudyDetail>(`${study(id)}/reconcile`),
  reconcileRecent: () => api.post<PacsReconciliationSummary>('/api/v1/radiology/pacs/reconcile'),
  viewer: (id: string) => api.get<ViewerLink>(`${study(id)}/viewer`),
  reviewAiFinding: (findingId: string, acknowledge: boolean, note: string | null) =>
    api.post<AiFindingDto>(`/api/v1/radiology/ai-findings/${findingId}/review`, { acknowledge, note }),

  saveReport: (id: string, request: SaveRadiologyReportRequest) =>
    api.put<RadiologyReportDto>(`${study(id)}/report`, request),
  submitReport: (id: string) => api.post<RadiologyReportDto>(`${study(id)}/report/submit`),
  verifyReport: (id: string, criticalCall: CriticalCallDetails | null, release: boolean) =>
    api.post<RadiologyReportDto>(`${study(id)}/report/verify`, { criticalCall, release }),
  releaseReport: (id: string) => api.post<RadiologyReportDto>(`${study(id)}/report/release`),
  addAddendum: (id: string, request: AddAddendumRequest) =>
    api.post<RadiologyReportDto>(`${study(id)}/report/addenda`, request),

  saveTemplate: (id: string | null, request: SaveReportTemplateRequest): Promise<{ id: string } | undefined> =>
    id
      ? api.put<undefined>(`/api/v1/radiology/templates/${id}`, request)
      : api.post<{ id: string }>('/api/v1/radiology/templates', request),
  saveProcedure: (id: string | null, request: SaveImagingProcedureRequest): Promise<{ id: string } | undefined> =>
    id
      ? api.put<undefined>(`/api/v1/radiology/procedures/${id}`, request)
      : api.post<{ id: string }>('/api/v1/radiology/procedures', request),
};

/** The A4 radiology report as the server prints it and makes its PDF (see `@/lib/documents`). */
export const radiologyReportDocument = (studyId: string) => `${study(studyId)}/report`;

export const RADIOLOGY_PERMISSIONS = {
  read: 'radiology.studies.read',
  worklist: 'radiology.worklist',
  write: 'radiology.reports.write',
  verify: 'radiology.reports.verify',
  mastersManage: 'masters.manage',
  /** See a study's images, in HC360 and in the PACS viewer (doctors, radiographers and radiologists). */
  images: 'radiology.images.view',
} as const;

/** Who may open imaging studies and resolve a report id to its study (server `RadiologyEndpoints.StudyReaders`). */
export const RADIOLOGY_STUDY_READERS = [
  RADIOLOGY_PERMISSIONS.read,
  RADIOLOGY_PERMISSIONS.worklist,
  RADIOLOGY_PERMISSIONS.write,
] as const;
