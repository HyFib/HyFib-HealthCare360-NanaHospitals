import type { Address, BloodGroup, Gender, MaritalStatus, PagedResult, PayerType } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { photoDataUrl } from './photo';

export const PATIENT_CATEGORIES = [
  'General',
  'Staff',
  'StaffDependent',
  'SeniorCitizen',
  'Vip',
  'Corporate',
  'Insurance',
  'GovernmentScheme',
  'BelowPovertyLine',
  'Charity',
  'International',
] as const;
export type PatientCategory = (typeof PATIENT_CATEGORIES)[number];

export const GUARDIAN_RELATIONS = [
  'SonOf',
  'DaughterOf',
  'WifeOf',
  'HusbandOf',
  'FatherOf',
  'MotherOf',
  'Guardian',
] as const;
export type GuardianRelation = (typeof GUARDIAN_RELATIONS)[number];

export const guardianRelationLabel: Record<GuardianRelation, string> = {
  SonOf: 'S/o',
  DaughterOf: 'D/o',
  WifeOf: 'W/o',
  HusbandOf: 'H/o',
  FatherOf: 'F/o',
  MotherOf: 'M/o',
  Guardian: 'Guardian',
};

export interface PatientListItem {
  id: string;
  uhid: string;
  fullName: string;
  gender: Gender;
  dateOfBirth: string | null;
  age: string | null;
  phoneMasked: string | null;
  category: PatientCategory;
  isVip: boolean;
  isDeceased: boolean;
  registeredAt: string;
  lastVisitAt: string | null;
}

export interface PatientDetail {
  id: string;
  uhid: string;
  title: string | null;
  firstName: string;
  middleName: string | null;
  lastName: string | null;
  fullName: string;
  gender: Gender;
  dateOfBirth: string | null;
  isDateOfBirthEstimated: boolean;
  age: string | null;
  bloodGroup: BloodGroup;
  maritalStatus: MaritalStatus;
  phone: string | null;
  alternatePhone: string | null;
  email: string | null;
  address: Address;
  guardianName: string | null;
  guardianRelation: GuardianRelation | null;
  emergencyContactName: string | null;
  emergencyContactPhone: string | null;
  emergencyContactRelation: string | null;
  occupation: string | null;
  religion: string | null;
  nationality: string | null;
  preferredLanguage: string | null;
  category: PatientCategory;
  abhaNumberMasked: string | null;
  abhaAddress: string | null;
  aadhaarLast4: string | null;
  photoFileId: string | null;
  registeredFacilityId: string;
  registeredAt: string;
  isVip: boolean;
  isDeceased: boolean;
  deceasedAt: string | null;
  mergedIntoPatientId: string | null;
  notes: string | null;
  isActive: boolean;
  version: number;
}

export interface PatientSummary {
  id: string;
  uhid: string;
  fullName: string;
  gender: Gender;
  dateOfBirth: string | null;
  age: string | null;
  phone: string | null;
  bloodGroup: BloodGroup;
  category: PatientCategory;
  isVip: boolean;
  isDeceased: boolean;
  photoFileId: string | null;
}

/** Fields shared by registration and update (the server validates everything again). */
export interface PatientFormPayload {
  title: string | null;
  firstName: string;
  middleName: string | null;
  lastName: string | null;
  gender: Gender;
  dateOfBirth: string | null;
  ageYears: number | null;
  ageMonths: number | null;
  phone: string | null;
  alternatePhone: string | null;
  email: string | null;
  address: Address | null;
  bloodGroup: BloodGroup | null;
  maritalStatus: MaritalStatus | null;
  guardianName: string | null;
  guardianRelation: GuardianRelation | null;
  emergencyContactName: string | null;
  emergencyContactPhone: string | null;
  emergencyContactRelation: string | null;
  occupation: string | null;
  religion: string | null;
  nationality: string | null;
  preferredLanguage: string | null;
  category: PatientCategory | null;
  abhaNumber: string | null;
  abhaAddress: string | null;
  aadhaarNumber: string | null;
  photoFileId: string | null;
  isVip: boolean;
  notes: string | null;
}

export interface RegisterPatientRequest extends PatientFormPayload {
  confirmedNotDuplicate: boolean;
}

export interface UpdatePatientRequest extends PatientFormPayload {
  isDeceased: boolean;
  deceasedAt: string | null;
  isActive: boolean;
  version: number;
  /**
   * `photoFileId` null keeps the patient's photo (the server never clears it because a save left it out); `removePhoto`
   * takes it away. See `photoChange`.
   */
  removePhoto: boolean;
}

export interface DuplicateCandidate {
  id: string;
  uhid: string;
  fullName: string;
  gender: Gender;
  age: string | null;
  phoneMasked: string | null;
  score: number;
  reason: string;
}

export interface DuplicateCheckRequest {
  firstName: string | null;
  lastName: string | null;
  dateOfBirth: string | null;
  phone: string | null;
  abhaNumber: string | null;
  aadhaarNumber: string | null;
}

export type VisitType = 'OutPatient' | 'InPatient' | 'Emergency' | 'DayCare' | 'Telehealth' | 'HealthCheckup';
export type VisitStatus = 'Open' | 'InProgress' | 'Completed' | 'Cancelled';
export type TokenStatus = 'Waiting' | 'Called' | 'InService' | 'Completed' | 'Skipped' | 'NoShow' | 'Cancelled';
export type TokenPriority = 'Normal' | 'Priority' | 'Emergency';
export type ReferralSource =
  'Self' | 'Doctor' | 'Hospital' | 'HealthCamp' | 'Online' | 'Corporate' | 'Advertisement' | 'Other';

export interface VisitListItem {
  id: string;
  visitNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  type: VisitType;
  status: VisitStatus;
  departmentId: string | null;
  departmentName: string | null;
  practitionerId: string | null;
  practitionerName: string | null;
  visitedAt: string;
  completedAt: string | null;
  isFollowUp: boolean;
  isFreeFollowUp: boolean;
  payerType: PayerType;
  isMlc: boolean;
  tokenLabel: string | null;
  tokenStatus: TokenStatus | null;
}

export interface QueueTokenDto {
  id: string;
  visitId: string;
  patientId: string;
  departmentId: string;
  practitionerId: string | null;
  tokenDate: string;
  sequence: number;
  label: string;
  status: TokenStatus;
  priority: TokenPriority;
  issuedAt: string;
  calledAt: string | null;
  serviceStartedAt: string | null;
  completedAt: string | null;
  room: string | null;
  callCount: number;
}

export interface VisitDetail {
  id: string;
  visitNumber: string;
  patientId: string;
  type: VisitType;
  status: VisitStatus;
  departmentId: string | null;
  practitionerId: string | null;
  appointmentId: string | null;
  visitedAt: string;
  completedAt: string | null;
  isFollowUp: boolean;
  isFreeFollowUp: boolean;
  previousVisitId: string | null;
  payerType: PayerType;
  payerId: string | null;
  referralSource: ReferralSource;
  referredBy: string | null;
  chiefComplaint: string | null;
  isMlc: boolean;
  mlcNumber: string | null;
  notes: string | null;
  cancelReason: string | null;
  token: QueueTokenDto | null;
  version: number;
}

export interface CreateVisitRequest {
  patientId: string;
  type: VisitType;
  departmentId: string | null;
  practitionerId: string | null;
  appointmentId: string | null;
  payerType: PayerType;
  payerId: string | null;
  referralSource: ReferralSource;
  referredBy: string | null;
  chiefComplaint: string | null;
  isMlc: boolean;
  mlcNumber: string | null;
  notes: string | null;
  priority: TokenPriority;
  issueToken: boolean;
  notifyPatient: boolean;
}

export const CONSENT_TYPES = [
  'GeneralTreatment',
  'DataSharing',
  'Telehealth',
  'Research',
  'SmsWhatsAppUpdates',
  'MarketingCommunication',
  'Photography',
  'HighRiskProcedure',
  'Anaesthesia',
  'BloodTransfusion',
] as const;
export type ConsentType = (typeof CONSENT_TYPES)[number];

/** A recorded consent (EHR-06), newest first. A withdrawn consent stays as history. */
export interface ConsentDto {
  id: string;
  type: ConsentType;
  consentVersion: string;
  granted: boolean;
  capturedAt: string;
  /** Null when no staff member recorded it (for example a reply on WhatsApp). */
  capturedByName: string | null;
  witnessName: string | null;
  signedByRelation: string | null;
  signedByName: string | null;
  documentFileId: string | null;
  revokedAt: string | null;
  revokedReason: string | null;
  /** Null when no staff member withdrew it (for example a STOP reply on WhatsApp). */
  revokedByName: string | null;
  notes: string | null;
  /** Given and not withdrawn. */
  isActive: boolean;
}

export interface CaptureConsentRequest {
  type: ConsentType;
  /** At most 16 characters; the server records "1.0" when it is null. */
  consentVersion: string | null;
  granted: boolean;
  witnessName: string | null;
  signedByRelation: string | null;
  /** Required when `signedByRelation` is given. */
  signedByName: string | null;
  documentFileId: string | null;
  notes: string | null;
}

export interface PatientDocumentDto {
  id: string;
  fileId: string;
  type: string;
  title: string;
  documentDate: string | null;
  source: string | null;
  notes: string | null;
  createdAt: string;
  fileName: string | null;
  contentType: string | null;
  sizeBytes: number | null;
}

export interface PatientSearchParams {
  search?: string;
  page?: number;
  pageSize?: number;
  sort?: string;
  desc?: boolean;
  gender?: Gender;
  category?: PatientCategory;
}

export const patientKeys = {
  all: ['patients'] as const,
  search: (params: PatientSearchParams) => ['patients', 'search', params] as const,
  detail: (id: string) => ['patients', 'detail', id] as const,
  visits: (id: string, page: number) => ['patients', 'visits', id, page] as const,
  consents: (id: string) => ['patients', 'consents', id] as const,
  documents: (id: string) => ['patients', 'documents', id] as const,
  photo: (fileId: string) => ['patients', 'photo', fileId] as const,
};

export const patientQueries = {
  search: (params: PatientSearchParams) =>
    queryOptions({
      queryKey: patientKeys.search(params),
      queryFn: ({ signal }) =>
        api.get<PagedResult<PatientListItem>>('/api/v1/patients', { query: { ...params }, signal }),
      placeholderData: keepPreviousData,
    }),
  detail: (id: string) =>
    queryOptions({
      queryKey: patientKeys.detail(id),
      queryFn: ({ signal }) => api.get<PatientDetail>(`/api/v1/patients/${id}`, { signal }),
    }),
  visits: (id: string, page = 1) =>
    queryOptions({
      queryKey: patientKeys.visits(id, page),
      queryFn: ({ signal }) =>
        api.get<PagedResult<VisitListItem>>(`/api/v1/patients/${id}/visits`, { query: { page, pageSize: 10 }, signal }),
      placeholderData: keepPreviousData,
    }),
  consents: (id: string) =>
    queryOptions({
      queryKey: patientKeys.consents(id),
      queryFn: ({ signal }) => api.get<ConsentDto[]>(`/api/v1/patients/${id}/consents`, { signal }),
    }),
  documents: (id: string) =>
    queryOptions({
      queryKey: patientKeys.documents(id),
      queryFn: ({ signal }) => api.get<PatientDocumentDto[]>(`/api/v1/patients/${id}/documents`, { signal }),
    }),
  /** A stored photo as an image source, or null when it cannot be read. A photo file never changes, so it is kept. */
  photo: (fileId: string) =>
    queryOptions({
      queryKey: patientKeys.photo(fileId),
      queryFn: () => photoDataUrl(fileId),
      staleTime: Infinity,
      gcTime: 30 * 60_000,
      retry: false,
    }),
};

export const patientApi = {
  register: (request: RegisterPatientRequest) => api.post<{ id: string; uhid: string }>('/api/v1/patients', request),
  update: (id: string, request: UpdatePatientRequest) => api.put<void>(`/api/v1/patients/${id}`, request),
  duplicates: (request: DuplicateCheckRequest, signal?: AbortSignal) =>
    api.post<DuplicateCandidate[]>('/api/v1/patients/duplicates', request, { signal }),
  breakGlass: (id: string, reason: string) =>
    api.post<{ patientId: string; expiresAt: string }>(`/api/v1/patients/${id}/break-glass`, { reason }),
  createVisit: (request: CreateVisitRequest) => api.post<VisitDetail>('/api/v1/visits', request),
};

/** Recording and withdrawing consents needs `patients.consents`; a restricted record answers 403 `patient.restricted`. */
export const consentApi = {
  capture: (patientId: string, request: CaptureConsentRequest) =>
    api.post<{ id: string }>(`/api/v1/patients/${patientId}/consents`, request),
  /** 422 `consent.revoked` when it is already withdrawn, `consent.not_granted` for a refusal. */
  revoke: (consentId: string, reason: string) =>
    api.post<void>(`/api/v1/patients/consents/${consentId}/revoke`, { reason }),
};
