import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { FacilityLetterhead, PrescriberInfo } from './api';
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

export interface PrescriptionPrintLine {
  sequence: number;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string;
  frequency: string;
  frequencyEnglish: string;
  frequencyHindi: string;
  durationDays: number;
  quantity: number;
  instructions: string | null;
  isControlledDrug: boolean;
}

export interface PrescriptionPrintModel {
  id: string;
  prescriptionNumber: string;
  status: PrescriptionStatus;
  signedAt: string | null;
  letterhead: FacilityLetterhead | null;
  prescriber: PrescriberInfo | null;
  patient: { id: string; uhid: string; name: string; age: string | null; gender: Gender; preferredLanguage: string | null };
  diagnoses: string[];
  knownAllergies: string[];
  lines: PrescriptionPrintLine[];
  advice: string | null;
  followUpDate: string | null;
  followUpAdvice: string | null;
  refillsAllowed: number;
  verificationToken: string | null;
  /** Server-relative verification API; resolves only on the hospital network. */
  verificationPath: string | null;
  /** Absolute portal verification page (`{portal.baseUrl}/portal/verify/…`) for the QR code; null until the public address is configured. */
  verificationUrl: string | null;
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
  list: (patientId: string, params: PrescriptionListParams) => ['emr', 'patient', patientId, 'prescriptions', params] as const,
  detail: (id: string) => ['emr', 'prescription', id] as const,
  interactions: (search: string, severity: InteractionSeverity | null, page: number) => ['emr', 'drug-interactions', search, severity ?? 'all', page] as const,
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
        api.get<PagedResult<ControlledDrugRegisterDto>>('/api/v1/clinical/controlled-drug-register', { query: { from, to, page, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  /** Prescriber drug search (needs `clinical.prescriptions.write`), with the facility's pharmacy stock. */
  drugs: (search: string) =>
    queryOptions({
      queryKey: prescriptionKeys.drugs(search),
      queryFn: ({ signal }) => api.get<PrescribableDrug[]>('/api/v1/emr/drugs', { query: { search, take: 15 }, signal }),
      staleTime: 60_000,
    }),
};

export const prescriptionsApi = {
  create: (request: CreatePrescriptionRequest) => api.post<PrescriptionDto>('/api/v1/clinical/prescriptions', request),
  update: (id: string, request: UpdatePrescriptionRequest) => api.put<PrescriptionDto>(`/api/v1/clinical/prescriptions/${id}`, request),
  check: (id: string) => api.post<SafetyCheckResponse>(`/api/v1/clinical/prescriptions/${id}/check`),
  sign: (id: string, request: SignPrescriptionRequest) => api.post<SignPrescriptionResult>(`/api/v1/clinical/prescriptions/${id}/sign`, request),
  cancel: (id: string, reason: string) => api.post<PrescriptionDto>(`/api/v1/clinical/prescriptions/${id}/cancel`, { reason }),
  repeat: (id: string) => api.post<PrescriptionDto>(`/api/v1/clinical/prescriptions/${id}/repeat`),
  refill: (id: string, request: Omit<SignPrescriptionRequest, 'version'>) =>
    api.post<SignPrescriptionResult>(`/api/v1/clinical/prescriptions/${id}/refill`, request),
  print: (id: string) => api.get<PrescriptionPrintModel>(`/api/v1/clinical/prescriptions/${id}/print`),
};

export const safetyTablesApi = {
  saveInteraction: (id: string | null, request: SaveDrugInteractionRequest) =>
    id ? api.put<DrugInteractionDto>(`/api/v1/clinical/drug-interactions/${id}`, request) : api.post<DrugInteractionDto>('/api/v1/clinical/drug-interactions', request),
  saveDoseLimit: (id: string | null, request: SaveDoseLimitRequest) =>
    id ? api.put<DoseLimitDto>(`/api/v1/clinical/dose-limits/${id}`, request) : api.post<DoseLimitDto>('/api/v1/clinical/dose-limits', request),
};
