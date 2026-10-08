import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  CriticalCallDetails,
  DiagnosticReportStatus,
  OrderPriority,
  PatientClass,
  ReportStudyRef,
} from '@/modules/lab/common/types';
import type { ViewerLink } from '@/modules/radiology/api';

// ------------------------------------------------------------------------------------------------ enums (as strings)

export const STUDY_TYPES = ['Ecg', 'Echo', 'Tmt', 'Holter', 'Abpm', 'Other'] as const;
export type CardiologyStudyType = (typeof STUDY_TYPES)[number];

export const CARDIOLOGY_STATUSES = [
  'Ordered',
  'Scheduled',
  'InProgress',
  'Acquired',
  'Reported',
  'Verified',
  'Released',
  'Cancelled',
] as const;
export type CardiologyStudyStatus = (typeof CARDIOLOGY_STATUSES)[number];

export const TMT_RESULTS = ['Negative', 'Positive', 'Inconclusive'] as const;
export type TmtResult = (typeof TMT_RESULTS)[number];

// ------------------------------------------------------------------------------------------------ measurements

/** 12-lead ECG (intervals in milliseconds). */
export interface EcgFindings {
  heartRate: number | null;
  rhythm: string | null;
  prInterval: number | null;
  qrsDuration: number | null;
  qtInterval: number | null;
  qtcInterval: number | null;
  axis: string | null;
  stTChanges: string | null;
  interpretation: string | null;
}

/** 2D echocardiography with colour Doppler. */
export interface EchoFindings {
  lvef: number | null;
  leftVentricle: string | null;
  rightVentricle: string | null;
  leftAtrium: string | null;
  rightAtrium: string | null;
  valves: string | null;
  rwma: string | null;
  diastolicFunction: string | null;
  pericardium: string | null;
  conclusion: string | null;
}

/** Treadmill (exercise stress) test. */
export interface TmtFindings {
  protocol: string | null;
  durationSeconds: number | null;
  mets: number | null;
  maxHeartRate: number | null;
  targetHeartRatePercent: number | null;
  bpResponse: string | null;
  symptoms: string | null;
  stChanges: string | null;
  reasonForTermination: string | null;
  result: TmtResult | null;
}

/** 24–48 hour Holter monitoring. */
export interface HolterFindings {
  durationHours: number | null;
  minHeartRate: number | null;
  meanHeartRate: number | null;
  maxHeartRate: number | null;
  ventricularEctopics: number | null;
  supraventricularEctopics: number | null;
  pauses: number | null;
  longestPause: string | null;
  arrhythmias: string | null;
  stChanges: string | null;
}

/** 24 hour ambulatory blood pressure (mmHg). */
export interface AbpmFindings {
  daytimeMeanSystolic: number | null;
  daytimeMeanDiastolic: number | null;
  nighttimeMeanSystolic: number | null;
  nighttimeMeanDiastolic: number | null;
  dippingPattern: string | null;
}

// ------------------------------------------------------------------------------------------------ studies and reports

export interface CardiologyStudyListItem {
  id: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  patientClass: PatientClass;
  studyName: string;
  studyType: CardiologyStudyType;
  priority: OrderPriority;
  status: CardiologyStudyStatus;
  orderedAt: string;
  scheduledAt: string | null;
  reportStatus: DiagnosticReportStatus | null;
  isCriticalFinding: boolean;
}

/** What the PACS did with a device file the last time it was sent (STOW-RS). */
export type CardiologyPacsStatus = 'Stored' | 'Refused' | 'Failed' | 'NotDicom';

export interface DeviceFilePacsDto {
  status: CardiologyPacsStatus;
  /** Why it was not stored, or the PACS's warning about one it stored. */
  message: string | null;
  sentAt: string;
}

export interface DeviceFileDto {
  id: string;
  fileName: string | null;
  contentType: string | null;
  sizeBytes: number | null;
  /** Null until the file is sent to the PACS (DICOM files only). */
  pacs: DeviceFilePacsDto | null;
}

export interface CardiologyReportDto {
  id: string;
  studyType: CardiologyStudyType;
  ecg: EcgFindings;
  echo: EchoFindings;
  tmt: TmtFindings;
  holter: HolterFindings;
  abpm: AbpmFindings;
  conclusion: string | null;
  recommendations: string | null;
  isCriticalFinding: boolean;
  criticalFindingNote: string | null;
  status: DiagnosticReportStatus;
  reportedBy: string | null;
  reportedAt: string | null;
  verifiedBy: string | null;
  verifiedAt: string | null;
  releasedAt: string | null;
  version: number;
}

export interface CardiologyStudyDetail {
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
  studyType: CardiologyStudyType;
  priority: OrderPriority;
  orderingPractitionerId: string | null;
  orderingDoctor: string | null;
  clinicalNotes: string | null;
  orderedAt: string;
  status: CardiologyStudyStatus;
  scheduledAt: string | null;
  room: string | null;
  startedAt: string | null;
  acquiredAt: string | null;
  technician: string | null;
  deviceName: string | null;
  technicianNotes: string | null;
  reportedAt: string | null;
  verifiedAt: string | null;
  releasedAt: string | null;
  cancelReason: string | null;
  chargeError: string | null;
  packageBookingId: string | null;
  quantity: number;
  files: DeviceFileDto[];
  report: CardiologyReportDto | null;
  version: number;
}

export interface SaveCardiologyReportRequest {
  ecg: EcgFindings | null;
  echo: EchoFindings | null;
  tmt: TmtFindings | null;
  holter: HolterFindings | null;
  abpm: AbpmFindings | null;
  conclusion: string | null;
  recommendations: string | null;
  isCriticalFinding: boolean;
  criticalFindingNote: string | null;
  version: number | null;
}

// ------------------------------------------------------------------------------------------------ queries and commands

export interface CardiologyFilters {
  from?: string;
  to?: string;
  status?: CardiologyStudyStatus;
  studyType?: CardiologyStudyType;
  patientId?: string;
  search?: string;
  page?: number;
}

const PAGE = 25;

export const cardiologyKeys = {
  all: ['cardiology'] as const,
  study: (id: string) => ['cardiology', 'studies', 'detail', id] as const,
  reportStudy: (reportId: string) => ['cardiology', 'report-study', reportId] as const,
};

export const cardiologyQueries = {
  studies: (filters: CardiologyFilters) =>
    queryOptions({
      queryKey: ['cardiology', 'studies', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<CardiologyStudyListItem>>('/api/v1/cardiology/studies', {
          query: { ...filters, pageSize: PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  study: (id: string) =>
    queryOptions({
      queryKey: cardiologyKeys.study(id),
      queryFn: ({ signal }) => api.get<CardiologyStudyDetail>(`/api/v1/cardiology/studies/${id}`, { signal }),
      refetchOnWindowFocus: false,
    }),
  reportingWorklist: (studyType?: CardiologyStudyType, page?: number) =>
    queryOptions({
      queryKey: ['cardiology', 'studies', 'reporting', studyType ?? 'all', page ?? 1],
      queryFn: ({ signal }) =>
        api.get<PagedResult<CardiologyStudyListItem>>('/api/v1/cardiology/reporting-worklist', {
          query: { studyType, page, pageSize: PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  /** The study of a report, for screens that hold only the report id (the doctor's results); 404 when not visible here. */
  reportStudy: (reportId: string) =>
    queryOptions({
      queryKey: cardiologyKeys.reportStudy(reportId),
      queryFn: ({ signal }) => api.get<ReportStudyRef>(`/api/v1/cardiology/reports/${reportId}`, { signal }),
      staleTime: 5 * 60_000,
    }),
};

const study = (id: string) => `/api/v1/cardiology/studies/${id}`;

export const cardiologyApi = {
  schedule: (id: string, scheduledAt: string, room: string | null) =>
    api.post<CardiologyStudyDetail>(`${study(id)}/schedule`, { scheduledAt, room }),
  start: (id: string, deviceName: string | null) =>
    api.post<CardiologyStudyDetail>(`${study(id)}/start`, { deviceName }),
  attachFiles: (id: string, fileIds: string[]) => api.post<CardiologyStudyDetail>(`${study(id)}/files`, { fileIds }),
  complete: (id: string, notes: string | null) => api.post<CardiologyStudyDetail>(`${study(id)}/complete`, { notes }),
  cancel: (id: string, reason: string) => api.post<CardiologyStudyDetail>(`${study(id)}/cancel`, { reason }),
  saveReport: (id: string, request: SaveCardiologyReportRequest) =>
    api.put<CardiologyStudyDetail>(`${study(id)}/report`, request),
  submitReport: (id: string) => api.post<CardiologyStudyDetail>(`${study(id)}/report/submit`),
  verifyReport: (id: string, criticalCall: CriticalCallDetails | null, release: boolean) =>
    api.post<CardiologyStudyDetail>(`${study(id)}/report/verify`, { criticalCall, release }),
  releaseReport: (id: string) => api.post<CardiologyStudyDetail>(`${study(id)}/report/release`),
  /** Sends the study's DICOM files the PACS does not hold yet, under the patient's UHID and the study. */
  sendToPacs: (id: string) => api.post<CardiologyStudyDetail>(`${study(id)}/pacs`),
  /** The PACS viewer link, once the PACS holds one of the study's files. */
  viewer: (id: string) => api.get<ViewerLink>(`${study(id)}/viewer`),
};

/** The A4 cardiology report as the server prints it and makes its PDF (see `@/lib/documents`). */
export const cardiologyReportDocument = (studyId: string) => `${study(studyId)}/report`;

export const CARDIOLOGY_PERMISSIONS = {
  read: 'cardiology.studies.read',
  manage: 'cardiology.studies.manage',
  verify: 'cardiology.reports.verify',
} as const;

/** Who may open cardiology studies and resolve a report id to its study (server `CardiologyEndpoints.StudyReaders`). */
export const CARDIOLOGY_STUDY_READERS = [
  CARDIOLOGY_PERMISSIONS.read,
  CARDIOLOGY_PERMISSIONS.manage,
  CARDIOLOGY_PERMISSIONS.verify,
] as const;
