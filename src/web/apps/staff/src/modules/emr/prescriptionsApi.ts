import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DoseFrequency } from './dosing';
import type { SafetyAlert, SafetyCheckResponse, SignPrescriptionRequest } from './safety';

export type PrescriptionStatus = 'Draft' | 'Signed' | 'Cancelled';
export type InteractionSeverity = 'Minor' | 'Moderate' | 'Major';

export interface PrescriptionItemRequest {
  drugItemId: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string;
  frequency: DoseFrequency;
  customFrequency: string | null;
  dosesPerDay: number | null;
  durationDays: number;
  quantity: number | null;
  instructions: string | null;
  isControlledDrug: boolean;
  controlledSchedule: string | null;
}

export interface PrescriptionItemDto extends Omit<PrescriptionItemRequest, 'quantity'> {
  id: string;
  sequence: number;
  frequencyText: string;
  quantity: number;
}

export interface PrescriptionDto {
  id: string;
  prescriptionNumber: string;
  patientId: string;
  encounterId: string | null;
  visitId: string | null;
  admissionId: string | null;
  practitionerId: string;
  practitionerName: string | null;
  isInpatient: boolean;
  status: PrescriptionStatus;
  prescribedAt: string;
  signedAt: string | null;
  notes: string | null;
  advice: string | null;
  refillsAllowed: number;
  refillsRemaining: number;
  refillOfPrescriptionId: string | null;
  safetyOverrideReason: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  items: PrescriptionItemDto[];
  version: number;
  /** Signed with the doctor's DSC (ERX-04); older servers leave these out. */
  isDigitallySigned?: boolean;
  digitallySignedAt?: string | null;
  digitalSignerName?: string | null;
  /**
   * False while the hospital requires the doctor's DSC and the prescription has no valid one: it is then not printed,
   * downloaded or shared (the pharmacy queue is not held back). Older servers leave it out: read that as true.
   */
  finalCopyAvailable?: boolean;
}

/** Whether the hospital's doctors sign prescriptions with their DSC (setting `clinical.prescriptions.signature`). */
export type PrescriptionSignaturePolicy = 'Off' | 'Optional' | 'Required';
/**
 * `Untrusted`: the prescription is as signed, but the certificate no longer chains to a CCA India root installed on the
 * server (the roots were changed since), so it is not shown as a DSC signature.
 */
export type DigitalSignatureState = 'NotSigned' | 'Valid' | 'Invalid' | 'Untrusted';

/**
 * The DSC signature as the server re-checked it now. `contentMatchesRecord` is false when the stored prescription no
 * longer gives the signed bytes; `chainStatus` is "Trusted" or why the certificate does not chain to a CCA India root
 * installed on the server, and `chainTrusted` says which (the server accepts a signature only from a trusted one).
 */
export interface PrescriptionDigitalSignatureDto {
  state: DigitalSignatureState;
  policy: PrescriptionSignaturePolicy;
  signedAt: string | null;
  signerName: string | null;
  signerSubject: string | null;
  certificateSerialNumber: string | null;
  certificateIssuer: string | null;
  certificateThumbprint: string | null;
  contentHash: string | null;
  contentMatchesRecord: boolean;
  chainStatus: string | null;
  /** Older servers leave it out. */
  chainTrusted?: boolean;
  message: string | null;
  /** The DSC rules let it be printed, downloaded and shared (see `PrescriptionDto.finalCopyAvailable`). */
  finalCopyAvailable?: boolean;
}

/** The DSC rules let a signed prescription be printed, downloaded and shared (ERX-04); older servers do not say. */
export const canIssueFinalCopy = (rx: Pick<PrescriptionDto, 'status' | 'finalCopyAvailable'>): boolean =>
  rx.status === 'Signed' && rx.finalCopyAvailable !== false;

/** The canonical bytes (base64) HC360 Desktop signs, with what the server says they contain. */
export interface PrescriptionSigningContent {
  prescriptionId: string;
  prescriptionNumber: string;
  format: string;
  content: string;
  contentHash: string;
  patientName: string;
  patientUhid: string;
  itemCount: number;
}

export interface PrescriptionListItem {
  id: string;
  prescriptionNumber: string;
  patientId: string;
  status: PrescriptionStatus;
  isInpatient: boolean;
  prescribedAt: string;
  signedAt: string | null;
  practitionerId: string;
  practitionerName: string | null;
  drugs: string[];
}

export interface CreatePrescriptionRequest {
  patientId: string;
  encounterId: string | null;
  visitId: string | null;
  admissionId: string | null;
  isInpatient: boolean;
  practitionerId: string | null;
  notes: string | null;
  advice: string | null;
  refillsAllowed: number;
  items: PrescriptionItemRequest[];
}

export interface UpdatePrescriptionRequest {
  notes: string | null;
  advice: string | null;
  refillsAllowed: number;
  items: PrescriptionItemRequest[];
  version: number;
}

export interface SignPrescriptionResult {
  prescription: PrescriptionDto;
  alerts: SafetyAlert[];
}

export interface DrugInteractionDto {
  id: string;
  drugA: string;
  drugB: string;
  severity: InteractionSeverity;
  effect: string;
  management: string | null;
  isActive: boolean;
  version: number;
}

export interface SaveDrugInteractionRequest {
  drugA: string;
  drugB: string;
  severity: InteractionSeverity;
  effect: string;
  management: string | null;
  isActive: boolean;
  version: number | null;
}

export interface DoseLimitDto {
  id: string;
  genericName: string;
  maxDailyDoseMg: number;
  route: string | null;
  notes: string | null;
  isActive: boolean;
  version: number;
}

export interface SaveDoseLimitRequest {
  genericName: string;
  maxDailyDoseMg: number;
  route: string | null;
  notes: string | null;
  isActive: boolean;
  version: number | null;
}

export interface ControlledDrugRegisterDto {
  id: string;
  prescriptionId: string;
  prescriptionNumber: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  practitionerId: string;
  practitionerName: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  schedule: string | null;
  quantity: number;
  prescribedAt: string;
  confirmedByUserId: string | null;
}

export type DrugSchedule = 'None' | 'Otc' | 'G' | 'H' | 'H1' | 'X';

/**
 * A medicine from the drug master for the prescriber's picker (`GET /api/v1/emr/drugs`, ERX-01). `isControlled` means
 * Schedule H1/X or NDPS (the lines that need the prescriber's confirmation and the controlled-drug register), with
 * `controlledSchedule` "X" or "H1". `available` is the stock of the facility's pharmacy stores: 0 when out of stock,
 * null when the facility has no pharmacy store.
 */
export interface PrescribableDrug {
  id: string;
  code: string;
  name: string;
  genericName: string | null;
  brandName: string | null;
  strength: string | null;
  dosageForm: string | null;
  schedule: DrugSchedule;
  isControlled: boolean;
  controlledSchedule: 'X' | 'H1' | null;
  requiresPrescription: boolean;
  isColdChain: boolean;
  baseUnit: string;
  available: number | null;
  nearestExpiry: string | null;
}

export interface PrescriptionListParams {
  encounterId?: string | null;
  status?: PrescriptionStatus;
  page?: number;
}

export const prescriptionKeys = {
  list: (patientId: string, params: PrescriptionListParams) =>
    ['emr', 'patient', patientId, 'prescriptions', params] as const,
  detail: (id: string) => ['emr', 'prescription', id] as const,
  signature: (id: string) => ['emr', 'prescription', id, 'signature'] as const,
  interactions: (search: string, severity: InteractionSeverity | null, page: number) =>
    ['emr', 'drug-interactions', search, severity ?? 'all', page] as const,
  doseLimits: () => ['emr', 'dose-limits'] as const,
  register: (from: string, to: string, page: number) => ['emr', 'controlled-register', from, to, page] as const,
  drugs: (search: string) => ['emr', 'drugs', search] as const,
};

export const prescriptionQueries = {
  list: (patientId: string, params: PrescriptionListParams = {}) =>
    queryOptions({
      queryKey: prescriptionKeys.list(patientId, params),
      queryFn: ({ signal }) =>
        api.get<PagedResult<PrescriptionListItem>>('/api/v1/clinical/prescriptions', {
          query: { patientId, encounterId: params.encounterId, status: params.status, page: params.page, pageSize: 20 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  detail: (id: string) =>
    queryOptions({
      queryKey: prescriptionKeys.detail(id),
      queryFn: ({ signal }) => api.get<PrescriptionDto>(`/api/v1/clinical/prescriptions/${id}`, { signal }),
    }),
  /** The DSC signature, re-checked by the server on every read. */
  signature: (id: string) =>
    queryOptions({
      queryKey: prescriptionKeys.signature(id),
      queryFn: ({ signal }) =>
        api.get<PrescriptionDigitalSignatureDto>(`/api/v1/clinical/prescriptions/${id}/digital-signature`, { signal }),
    }),
  interactions: (search: string, severity: InteractionSeverity | null, page: number) =>
    queryOptions({
      queryKey: prescriptionKeys.interactions(search, severity, page),
      queryFn: ({ signal }) =>
        api.get<PagedResult<DrugInteractionDto>>('/api/v1/clinical/drug-interactions', {
          query: { search: search || undefined, severity: severity ?? undefined, page, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  doseLimits: () =>
    queryOptions({
      queryKey: prescriptionKeys.doseLimits(),
      queryFn: ({ signal }) => api.get<DoseLimitDto[]>('/api/v1/clinical/dose-limits', { signal }),
    }),
  register: (from: string, to: string, page: number) =>
    queryOptions({
      queryKey: prescriptionKeys.register(from, to, page),
      queryFn: ({ signal }) =>
        api.get<PagedResult<ControlledDrugRegisterDto>>('/api/v1/clinical/controlled-drug-register', {
          query: { from, to, page, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  /** Prescriber drug search (needs `clinical.prescriptions.write`), with the facility's pharmacy stock. */
  drugs: (search: string) =>
    queryOptions({
      queryKey: prescriptionKeys.drugs(search),
      queryFn: ({ signal }) =>
        api.get<PrescribableDrug[]>('/api/v1/emr/drugs', { query: { search, take: 15 }, signal }),
      staleTime: 60_000,
    }),
};

export const prescriptionsApi = {
  create: (request: CreatePrescriptionRequest) => api.post<PrescriptionDto>('/api/v1/clinical/prescriptions', request),
  update: (id: string, request: UpdatePrescriptionRequest) =>
    api.put<PrescriptionDto>(`/api/v1/clinical/prescriptions/${id}`, request),
  check: (id: string) => api.post<SafetyCheckResponse>(`/api/v1/clinical/prescriptions/${id}/check`),
  sign: (id: string, request: SignPrescriptionRequest) =>
    api.post<SignPrescriptionResult>(`/api/v1/clinical/prescriptions/${id}/sign`, request),
  cancel: (id: string, reason: string) =>
    api.post<PrescriptionDto>(`/api/v1/clinical/prescriptions/${id}/cancel`, { reason }),
  repeat: (id: string) => api.post<PrescriptionDto>(`/api/v1/clinical/prescriptions/${id}/repeat`),
  refill: (id: string, request: Omit<SignPrescriptionRequest, 'version'>) =>
    api.post<SignPrescriptionResult>(`/api/v1/clinical/prescriptions/${id}/refill`, request),
  /** The bytes to sign with the DSC (prescribing doctor only). */
  signingContent: (id: string) =>
    api.get<PrescriptionSigningContent>(`/api/v1/clinical/prescriptions/${id}/signing-content`),
  /** Sends HC360 Desktop's detached CMS signature; the server checks it before keeping it. */
  attachSignature: (id: string, signature: string) =>
    api.post<PrescriptionDigitalSignatureDto>(`/api/v1/clinical/prescriptions/${id}/digital-signature`, { signature }),
};

/** The .p7s and the exact bytes it signs, for a check with OpenSSL (see docs/modules/clinical.md). */
export const prescriptionSignatureFiles = (id: string) => ({
  signature: `/api/v1/clinical/prescriptions/${id}/signature.p7s`,
  content: `/api/v1/clinical/prescriptions/${id}/signed-content`,
});

/** The A5 prescription as the server prints it and makes its PDF (see `@/lib/documents`). */
export const prescriptionDocument = (id: string) => `/api/v1/clinical/prescriptions/${id}`;

/**
 * A signed prescription as an ABDM health record: an NRCeS FHIR document (`application/fhir+json`, a file to save), while
 * ABDM health records are switched on, for staff with `abdm.export`, and, where the hospital requires the doctor's DSC,
 * only once the prescription carries a valid one (as for its PDF). Like the printed copy, it does not need the patient's
 * record-sharing consent.
 */
export const abdmPrescriptionBundle = (id: string) => `/api/v1/abdm/bundles/prescriptions/${id}`;

export const safetyTablesApi = {
  saveInteraction: (id: string | null, request: SaveDrugInteractionRequest) =>
    id
      ? api.put<DrugInteractionDto>(`/api/v1/clinical/drug-interactions/${id}`, request)
      : api.post<DrugInteractionDto>('/api/v1/clinical/drug-interactions', request),
  saveDoseLimit: (id: string | null, request: SaveDoseLimitRequest) =>
    id
      ? api.put<DoseLimitDto>(`/api/v1/clinical/dose-limits/${id}`, request)
      : api.post<DoseLimitDto>('/api/v1/clinical/dose-limits', request),
};
