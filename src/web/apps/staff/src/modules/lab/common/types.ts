/**
 * Types shared by the three diagnostics modules (laboratory, radiology, cardiology). They mirror the C# records in
 * HyFib.HealthCare360.Modules.Diagnostics (JSON camelCase, enums as strings, instants as ISO strings, DateOnly as
 * yyyy-MM-dd). Kept here so radiology and cardiology do not depend on the whole lab API surface.
 */
import type { Gender } from '@hc360/core';

export type OrderPriority = 'Routine' | 'Urgent' | 'Stat';

export const ORDER_PRIORITIES: readonly OrderPriority[] = ['Stat', 'Urgent', 'Routine'];

/** OP and External patients pay before the service; IP, ER and day-care work is billed to the running account. */
export type PatientClass = 'OutPatient' | 'InPatient' | 'Emergency' | 'DayCare' | 'External';

export const PATIENT_CLASSES: readonly PatientClass[] = ['OutPatient', 'InPatient', 'Emergency', 'DayCare', 'External'];

export type DiagnosticKind = 'Laboratory' | 'Radiology' | 'Cardiology';

/** Radiology and cardiology report workflow. */
export type DiagnosticReportStatus = 'Draft' | 'Reported' | 'Verified' | 'Released';

export type CommunicationMethod = 'Phone' | 'InPerson' | 'VideoCall' | 'SecureMessage';

/** Documented call for a critical value or finding (read-back is mandatory). */
export interface CriticalCallDetails {
  informedPersonName: string;
  informedPersonRole: string | null;
  informedPractitionerId: string | null;
  method: CommunicationMethod;
  informedAt: string;
  readBackConfirmed: boolean;
  notes: string | null;
}

export interface CriticalCallDto {
  id: string;
  kind: DiagnosticKind;
  patientId: string;
  uhid: string;
  patientName: string;
  finding: string;
  informedPersonName: string;
  informedPersonRole: string | null;
  method: CommunicationMethod;
  informedAt: string;
  readBackConfirmed: boolean;
  calledBy: string | null;
  notes: string | null;
  labResultId: string | null;
  imagingStudyId: string | null;
  cardiologyStudyId: string | null;
}

/** Turnaround summary in minutes (nearest-rank median and 90th percentile). */
export interface TatSummary {
  count: number;
  averageMinutes: number | null;
  medianMinutes: number | null;
  p90Minutes: number | null;
}

/** Facility letterhead printed on every report. */
export interface ReportLetterhead {
  facilityName: string;
  address: string | null;
  phone: string | null;
  email: string | null;
  nabhNumber: string | null;
  letterheadNote: string | null;
  logoFileId: string | null;
}

export interface ReportPatient {
  id: string;
  uhid: string;
  name: string;
  gender: Gender;
  age: string | null;
  dateOfBirth: string | null;
}

/** Who signed the report (qualification and council registration number are mandatory on Indian reports). */
export interface ReportSignatory {
  userId: string;
  name: string;
  qualification: string | null;
  registrationNumber: string | null;
  signatureFileId: string | null;
}

/** Stored file metadata returned by POST /api/v1/files. */
export interface StoredFileDto {
  id: string;
  fileName: string | null;
  contentType: string | null;
  sizeBytes: number | null;
  category: string;
  patientId: string | null;
  createdAt: string;
}
