import type {
  AadhaarCaptureSetup,
  Address,
  BloodGroup,
  Gender,
  MaritalStatus,
  PagedResult,
  PayerType,
} from '@hc360/core';
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
  /**
   * The phone numbers, email, street address and emergency contact's phone came masked: this role does not contact or bill
   * patients (`patients.contact.read`, SRS PRV-03), and so does not edit their details either.
   */
  contactsMasked?: boolean;
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
  /** The patient agreed at the desk to get reminders and updates on WhatsApp on `phone` (INT-WA). */
  whatsAppOptIn?: boolean;
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

/**
 * The patient's payer for the banner (`GET /api/v1/patients/{id}/payer`, UX-04), chosen by the server: the open visit's,
 * else the last visit's that was not cancelled (`isCurrentVisit: false`). The name, masked policy number and policy end
 * date come only to staff who may read payers and policies, for a payer other than self-pay.
 */
export interface PatientPayer {
  visitId: string;
  isCurrentVisit: boolean;
  payerType: PayerType;
  payerId: string | null;
  payerName: string | null;
  policyNumber: string | null;
  validUntil: string | null;
}

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

/** Where a WhatsApp opt-in was given. */
export type WhatsAppOptInSource = 'RegistrationDesk' | 'Portal' | 'WhatsAppStart';

/**
 * The WhatsApp choices of the patient's mobile number (INT-WA). They belong to the number, so family members registered
 * with it share them. Reminders and campaigns go on WhatsApp only when `reachable`; bills, reports and other messages
 * about the patient's care go unless `optedOut`.
 */
export interface WhatsAppConsentDto {
  /** Masked; null when the patient has no mobile number. */
  phone: string | null;
  optedIn: boolean;
  optedInAt: string | null;
  source: WhatsAppOptInSource | null;
  /** Who recorded the latest opt-in; null for a START reply. */
  capturedByName: string | null;
  /** A STOP reply (or the portal's "off") is in force. */
  optedOut: boolean;
  optedOutAt: string | null;
  reachable: boolean;
}

/** What the patient is to the family member who sees their records in the portal (PP-01). */
export const FAMILY_RELATIONS = ['Parent', 'Child', 'Spouse', 'Sibling', 'Grandparent', 'Grandchild', 'Other'] as const;
export type FamilyRelation = (typeof FAMILY_RELATIONS)[number];

/** Made by staff at the front desk, or in the portal with a code sent to the patient's own number. */
export type FamilyLinkSource = 'FrontDesk' | 'PortalCode';

/** A family member's portal account that sees the patient's records. An ended link stays as history. */
export interface PortalFamilyLinkDto {
  id: string;
  /** The number (E.164) the family member signs in to the portal with. */
  accountPhone: string;
  relation: FamilyRelation;
  source: FamilyLinkSource;
  linkedAt: string;
  linkedByName: string | null;
  /** Front desk: who agreed, the patient or their guardian. */
  consentGivenBy: string | null;
  /** Portal: the patient's number the code went to. */
  codeSentTo: string | null;
  revokedAt: string | null;
  revokedByName: string | null;
  revokedReason: string | null;
  isActive: boolean;
}

/** Who sees the patient's records in the portal: anyone signed in with the patient's own number, and the links. */
export interface PatientPortalAccessDto {
  ownPhone: string | null;
  links: PortalFamilyLinkDto[];
}

export interface LinkFamilyMemberRequest {
  accountPhone: string;
  relation: FamilyRelation;
  consentGivenBy: string;
  identityChecked: boolean;
}

export const PATIENT_DOCUMENT_TYPES = [
  'IdProof',
  'InsuranceCard',
  'ExternalReport',
  'ReferralLetter',
  'Consent',
  'Prescription',
  'DischargeSummary',
  'Imaging',
  'Photo',
  'Other',
] as const;
export type PatientDocumentType = (typeof PATIENT_DOCUMENT_TYPES)[number];

/** A document attached to the patient record (EHR-04), newest first. */
export interface PatientDocumentDto {
  id: string;
  fileId: string;
  type: PatientDocumentType;
  title: string;
  /** The date on the document ("yyyy-MM-dd"), not when it was attached (`createdAt`). */
  documentDate: string | null;
  /** Where the document came from, such as an outside lab or hospital. */
  source: string | null;
  notes: string | null;
  createdAt: string;
  fileName: string | null;
  contentType: string | null;
  sizeBytes: number | null;
}

/** Attaches a file already uploaded to the file store (`POST /api/v1/files`). */
export interface AttachDocumentRequest {
  fileId: string;
  type: PatientDocumentType;
  /** Required; at most 200 characters. */
  title: string;
  /** Not after today in the hospital (IST). */
  documentDate: string | null;
  /** At most 200 characters. */
  source: string | null;
  notes: string | null;
}

/** Why the two records are the same person: 10 to 500 characters, kept in the audit trail. */
export const MERGE_REASON_LIMITS = { min: 10, max: 500 } as const;

export interface MergePatientRequest {
  intoPatientId: string;
  reason: string;
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

export type IdentityCheckOutcome = 'Verified' | 'NotVerified' | 'Failed';

/**
 * A check of the patient's identity with their Aadhaar fingerprint (INT-BIO), as UIDAI answered it. The number itself is
 * never kept: only its last 4 digits, and whether a Virtual ID was used.
 */
export interface IdentityCheckDto {
  id: string;
  method: 'AadhaarFingerprint';
  outcome: IdentityCheckOutcome;
  message: string;
  aadhaarLast4: string;
  usedVirtualId: boolean;
  /** The check linked the Aadhaar number to a record that had none. */
  linkedAadhaar: boolean;
  /** `uidai`, or `simulator` for training. */
  provider: string;
  /** UIDAI's error code when the fingerprint did not match or could not be checked. */
  errorCode: string | null;
  /** HC360's transaction id, which UIDAI's answer repeats. */
  transactionId: string | null;
  /** UIDAI's response code: its reference for the check. */
  responseCode: string | null;
  deviceProviderId: string | null;
  deviceModelId: string | null;
  checkedAt: string;
  checkedByName: string | null;
}

export interface VerifyIdentityRequest {
  /** The 12-digit Aadhaar number or the 16-digit Virtual ID, as the patient gives it. */
  aadhaarNumber: string;
  /** The capture as the fingerprint reader encrypted it (`PidData`). */
  pidData: string;
  /** The patient agreed to Aadhaar authentication. */
  consent: boolean;
}

export const patientKeys = {
  all: ['patients'] as const,
  search: (params: PatientSearchParams) => ['patients', 'search', params] as const,
  detail: (id: string) => ['patients', 'detail', id] as const,
  visits: (id: string, page: number) => ['patients', 'visits', id, page] as const,
  visit: (visitId: string) => ['patients', 'visit', visitId] as const,
  /** Under the patient's visits, so whatever refreshes the visits refreshes the banner's payer too. */
  payer: (id: string) => ['patients', 'visits', id, 'payer'] as const,
  consents: (id: string) => ['patients', 'consents', id] as const,
  whatsApp: (id: string) => ['patients', 'whatsapp', id] as const,
  portalFamily: (id: string) => ['patients', 'portal-family', id] as const,
  documents: (id: string) => ['patients', 'documents', id] as const,
  photo: (fileId: string) => ['patients', 'photo', fileId] as const,
  identityChecks: (id: string) => ['patients', 'identity-checks', id] as const,
  identitySetup: ['patients', 'identity-setup'] as const,
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
  payer: (id: string) =>
    queryOptions({
      queryKey: patientKeys.payer(id),
      queryFn: ({ signal }) => api.get<{ current: PatientPayer | null }>(`/api/v1/patients/${id}/payer`, { signal }),
    }),
  visit: (visitId: string) =>
    queryOptions({
      queryKey: patientKeys.visit(visitId),
      queryFn: ({ signal }) => api.get<VisitDetail>(`/api/v1/visits/${visitId}`, { signal }),
    }),
  consents: (id: string) =>
    queryOptions({
      queryKey: patientKeys.consents(id),
      queryFn: ({ signal }) => api.get<ConsentDto[]>(`/api/v1/patients/${id}/consents`, { signal }),
    }),
  whatsApp: (id: string) =>
    queryOptions({
      queryKey: patientKeys.whatsApp(id),
      queryFn: ({ signal }) => api.get<WhatsAppConsentDto>(`/api/v1/patients/${id}/whatsapp`, { signal }),
    }),
  portalFamily: (id: string) =>
    queryOptions({
      queryKey: patientKeys.portalFamily(id),
      queryFn: ({ signal }) => api.get<PatientPortalAccessDto>(`/api/v1/patients/${id}/portal-family`, { signal }),
    }),
  documents: (id: string) =>
    queryOptions({
      queryKey: patientKeys.documents(id),
      queryFn: ({ signal }) => api.get<PatientDocumentDto[]>(`/api/v1/patients/${id}/documents`, { signal }),
    }),
  identityChecks: (id: string) =>
    queryOptions({
      queryKey: patientKeys.identityChecks(id),
      queryFn: ({ signal }) => api.get<IdentityCheckDto[]>(`/api/v1/patients/${id}/identity-checks`, { signal }),
    }),
  /** What the workstation should capture, or why identity cannot be checked here (needs `patients.identity.verify`). */
  identitySetup: () =>
    queryOptions({
      queryKey: patientKeys.identitySetup,
      queryFn: ({ signal }) => api.get<AadhaarCaptureSetup>('/api/v1/patients/identity-checks/setup', { signal }),
      staleTime: 60_000,
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
  /**
   * Merges the duplicate `id` into the surviving record `intoPatientId` (needs `patients.merge`): its visits, tokens,
   * consents, documents and identity checks move there, and it stays only as a pointer to it. It cannot be undone. The
   * server answers 422 `patient.merge_self` for the same record and `patient.already_merged` when either one has been
   * merged; the reason (10 to 500 characters) goes into the audit trail.
   */
  merge: (id: string, request: MergePatientRequest) => api.post<void>(`/api/v1/patients/${id}/merge`, request),
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
  /** Records that the patient told the desk they want WhatsApp messages; 422 `patient.no_mobile` without a number. */
  whatsAppOptIn: (patientId: string) => api.post<WhatsAppConsentDto>(`/api/v1/patients/${patientId}/whatsapp/opt-in`),
};

/**
 * Attaching a document needs `files.write` to upload the file and `patients.documents` to attach it; a restricted
 * record answers 403 `patient.restricted`.
 */
export const documentApi = {
  /** Stores the file encrypted, for this patient; the server checks what it is from its content, and takes up to 25 MB. */
  upload: (patientId: string, file: File) => {
    const data = new FormData();
    data.append('file', file);
    data.append('category', 'patient-document');
    data.append('patientId', patientId);
    return api.post<{ id: string }>('/api/v1/files', data, { timeoutMs: 120_000 });
  },
  attach: (patientId: string, request: AttachDocumentRequest) =>
    api.post<{ id: string }>(`/api/v1/patients/${patientId}/documents`, request),
  /** Takes the document off the record (needs `patients.documents`); 404 when it is already gone. */
  remove: (documentId: string) => api.delete<void>(`/api/v1/patients/documents/${documentId}`),
};

/**
 * Sharing the patient's records with a family member's portal account needs `patients.consents`. Linking answers 422
 * `portal.family_same_number` for the patient's own number and 409 `portal.family_already_linked`; ending answers 422
 * `portal.family_link_ended` when it has already ended.
 */
export const portalFamilyApi = {
  link: (patientId: string, request: LinkFamilyMemberRequest) =>
    api.post<{ id: string }>(`/api/v1/patients/${patientId}/portal-family`, request),
  revoke: (linkId: string, reason: string) =>
    api.post<void>(`/api/v1/patients/portal-family/${linkId}/revoke`, { reason }),
};

/**
 * Checking the patient's identity with their Aadhaar fingerprint needs `patients.identity.verify`. The server answers
 * 422 `patient.aadhaar_mismatch` for another number than the record's, 409 `patient.aadhaar_in_use` for a number linked
 * to another patient, and 422 `patient.identity_check_failed` when UIDAI was not asked (not set up, an unusable capture).
 */
export const identityCheckApi = {
  verify: (patientId: string, request: VerifyIdentityRequest) =>
    api.post<IdentityCheckDto>(`/api/v1/patients/${patientId}/identity-checks`, request),
};
