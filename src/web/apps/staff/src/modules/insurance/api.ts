import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ServiceCategory } from '@/modules/billing/labels';
import type { ClaimStatus, EnhancementStatus, PayerCategory, PreAuthStatus } from './labels';

/*
 * Insurance / TPA API (/api/v1/insurance, Billing module Features/Insurance). Types mirror the C# records; policy
 * numbers are always masked — revealing one is a separate, audited call.
 */

// ------------------------------------------------------------------ payers and non-payable items

export interface PayerListItem {
  id: string;
  code: string;
  name: string;
  category: PayerCategory;
  contactPerson: string | null;
  phone: string | null;
  tariffPlanId: string | null;
  tariffPlanName: string | null;
  creditDays: number;
  isActive: boolean;
}

export interface PayerDetail {
  id: string;
  code: string;
  name: string;
  category: PayerCategory;
  contactPerson: string | null;
  phone: string | null;
  email: string | null;
  address: string | null;
  gstin: string | null;
  tariffPlanId: string | null;
  creditDays: number;
  defaultCoPayPercent: number;
  portalUrl: string | null;
  isActive: boolean;
  notes: string | null;
  version: number;
}

export interface SavePayerRequest {
  code: string;
  name: string;
  category: PayerCategory;
  contactPerson: string | null;
  phone: string | null;
  email: string | null;
  address: string | null;
  gstin: string | null;
  tariffPlanId: string | null;
  creditDays: number;
  defaultCoPayPercent: number;
  portalUrl: string | null;
  isActive: boolean;
  notes: string | null;
  version: number | null;
}

export interface NonPayableItemDto {
  id: string;
  payerId: string | null;
  payerName: string | null;
  serviceItemId: string | null;
  serviceCode: string | null;
  serviceName: string | null;
  category: ServiceCategory | null;
  notes: string | null;
}

export interface SaveNonPayableItemRequest {
  payerId: string | null;
  serviceItemId: string | null;
  category: ServiceCategory | null;
  notes: string | null;
}

// ------------------------------------------------------------------ policies

export interface PolicyDto {
  id: string;
  patientId: string;
  payerId: string;
  payerName: string | null;
  tpaId: string | null;
  tpaName: string | null;
  policyNumberMasked: string;
  memberId: string | null;
  policyHolderName: string | null;
  relationToHolder: string | null;
  validFrom: string;
  validTo: string;
  sumInsured: number;
  coPayPercent: number;
  isActive: boolean;
  isValidToday: boolean;
  verifiedAt: string | null;
  verificationNotes: string | null;
  cardFileId: string | null;
  notes: string | null;
  version: number;
}

export interface SavePolicyRequest {
  patientId: string;
  payerId: string;
  tpaId: string | null;
  /** Required on create; null on update keeps the stored number. */
  policyNumber: string | null;
  memberId: string | null;
  policyHolderName: string | null;
  relationToHolder: string | null;
  validFrom: string;
  validTo: string;
  sumInsured: number;
  coPayPercent: number;
  isActive: boolean;
  cardFileId: string | null;
  notes: string | null;
  version: number | null;
}

export interface PolicyNumberDto {
  policyId: string;
  policyNumber: string;
}

// ------------------------------------------------------------------ pre-authorisations

export interface PreAuthListItem {
  id: string;
  number: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  admissionId: string | null;
  payerId: string;
  payerName: string | null;
  requestedAmount: number;
  effectiveApprovedAmount: number;
  status: PreAuthStatus;
  createdAt: string;
  submittedAt: string | null;
}

export interface EnhancementDto {
  id: string;
  requestedAmount: number;
  reason: string;
  status: EnhancementStatus;
  approvedAmount: number;
  reference: string | null;
  requestedAt: string;
  respondedAt: string | null;
}

export interface PreAuthDetail {
  id: string;
  number: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  admissionId: string | null;
  visitId: string | null;
  policyId: string;
  policyNumberMasked: string | null;
  payerId: string;
  payerName: string | null;
  tpaId: string | null;
  tpaName: string | null;
  requestedAmount: number;
  diagnosisCodes: string | null;
  procedureCodes: string | null;
  clinicalSummary: string | null;
  expectedStayDays: number | null;
  documentFileIds: string[];
  status: PreAuthStatus;
  approvedAmount: number;
  approvalReference: string | null;
  finalApprovedAmount: number | null;
  effectiveApprovedAmount: number;
  queryText: string | null;
  queryResponse: string | null;
  rejectionReason: string | null;
  submittedAt: string | null;
  approvedAt: string | null;
  rejectedAt: string | null;
  enhancements: EnhancementDto[];
  version: number;
}

export interface SavePreAuthRequest {
  policyId: string;
  admissionId: string | null;
  visitId: string | null;
  requestedAmount: number;
  diagnosisCodes: string | null;
  procedureCodes: string | null;
  clinicalSummary: string | null;
  expectedStayDays: number | null;
  documentFileIds: string[] | null;
  version: number | null;
}

export interface PreAuthFilters {
  status?: PreAuthStatus;
  patientId?: string;
  admissionId?: string;
  search?: string;
  page?: number;
}

// ------------------------------------------------------------------ claims and ageing

export interface ClaimListItem {
  id: string;
  claimNumber: string;
  billId: string;
  billNumber: string | null;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  payerId: string;
  payerName: string | null;
  claimedAmount: number;
  approvedAmount: number | null;
  settledAmount: number;
  deductionAmount: number;
  status: ClaimStatus;
  submittedAt: string;
}

export interface ClaimSettlementDto {
  id: string;
  amount: number;
  deductionAmount: number;
  utrNumber: string | null;
  reference: string | null;
  settledOn: string;
  receiptId: string | null;
}

export interface ClaimDeductionDto {
  id: string;
  settlementId: string | null;
  reason: string;
  amount: number;
}

export interface ClaimDetail {
  id: string;
  claimNumber: string;
  billId: string;
  billNumber: string | null;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  payerId: string;
  payerName: string | null;
  tpaId: string | null;
  policyId: string | null;
  preAuthorizationId: string | null;
  claimedAmount: number;
  diagnosisCodes: string | null;
  procedureCodes: string | null;
  documentFileIds: string[];
  status: ClaimStatus;
  submittedAt: string;
  payerClaimReference: string | null;
  queryText: string | null;
  queryResponse: string | null;
  approvedAmount: number | null;
  settledAmount: number;
  deductionAmount: number;
  outstanding: number;
  utrNumber: string | null;
  rejectionReason: string | null;
  settlements: ClaimSettlementDto[];
  deductions: ClaimDeductionDto[];
  version: number;
}

export interface CreateClaimRequest {
  billId: string;
  claimedAmount: number | null;
  diagnosisCodes: string | null;
  procedureCodes: string | null;
  documentFileIds: string[] | null;
  payerClaimReference: string | null;
}

export interface UpdateClaimCodingRequest {
  diagnosisCodes: string | null;
  procedureCodes: string | null;
  documentFileIds: string[] | null;
  payerClaimReference: string | null;
  version: number;
}

export interface SettleClaimRequest {
  amount: number;
  deductions: { reason: string; amount: number }[] | null;
  utrNumber: string | null;
  reference: string | null;
  settledOn: string | null;
}

export interface ClaimFilters {
  status?: ClaimStatus;
  payerId?: string;
  patientId?: string;
  search?: string;
  page?: number;
}

export interface AgeingRow {
  payerId: string;
  payerName: string;
  category: PayerCategory | null;
  creditDays: number;
  days0To30: number;
  days31To60: number;
  days61To90: number;
  over90: number;
  total: number;
}

export interface ReceivablesAgeingReport {
  asOf: string;
  payers: AgeingRow[];
  days0To30: number;
  days31To60: number;
  days61To90: number;
  over90: number;
  total: number;
}

// ------------------------------------------------------------------ queries

export const insuranceKeys = {
  all: ['insurance'] as const,
  payers: ['insurance', 'payers'] as const,
  nonPayables: ['insurance', 'non-payables'] as const,
  policies: ['insurance', 'policies'] as const,
  preAuths: ['insurance', 'preauths'] as const,
  claims: ['insurance', 'claims'] as const,
};

export const insuranceQueries = {
  payers: (options: { category?: PayerCategory; includeInactive?: boolean; search?: string } = {}) =>
    queryOptions({
      queryKey: [...insuranceKeys.payers, 'list', options],
      queryFn: ({ signal }) => api.get<PayerListItem[]>('/api/v1/insurance/payers', { query: { ...options }, signal }),
      staleTime: 5 * 60_000,
    }),
  payer: (id: string) =>
    queryOptions({
      queryKey: [...insuranceKeys.payers, 'detail', id],
      queryFn: ({ signal }) => api.get<PayerDetail>(`/api/v1/insurance/payers/${id}`, { signal }),
    }),
  nonPayables: (payerId?: string) =>
    queryOptions({
      queryKey: [...insuranceKeys.nonPayables, payerId ?? 'all'],
      queryFn: ({ signal }) => api.get<NonPayableItemDto[]>('/api/v1/insurance/non-payable-items', { query: { payerId }, signal }),
    }),
  policies: (patientId: string, includeInactive = false) =>
    queryOptions({
      queryKey: [...insuranceKeys.policies, 'patient', patientId, includeInactive],
      queryFn: ({ signal }) => api.get<PolicyDto[]>('/api/v1/insurance/policies', { query: { patientId, includeInactive }, signal }),
    }),
  policy: (id: string) =>
    queryOptions({
      queryKey: [...insuranceKeys.policies, 'detail', id],
      queryFn: ({ signal }) => api.get<PolicyDto>(`/api/v1/insurance/policies/${id}`, { signal }),
    }),
  preAuths: (filters: PreAuthFilters) =>
    queryOptions({
      queryKey: [...insuranceKeys.preAuths, 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<PreAuthListItem>>('/api/v1/insurance/preauths', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  preAuth: (id: string) =>
    queryOptions({
      queryKey: [...insuranceKeys.preAuths, 'detail', id],
      queryFn: ({ signal }) => api.get<PreAuthDetail>(`/api/v1/insurance/preauths/${id}`, { signal }),
    }),
  claims: (filters: ClaimFilters) =>
    queryOptions({
      queryKey: [...insuranceKeys.claims, 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<ClaimListItem>>('/api/v1/insurance/claims', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  claimCount: (status: ClaimStatus) =>
    queryOptions({
      queryKey: [...insuranceKeys.claims, 'count', status],
      queryFn: async ({ signal }) => (await api.get<PagedResult<ClaimListItem>>('/api/v1/insurance/claims', { query: { status, pageSize: 1 }, signal })).totalCount,
      refetchInterval: 120_000,
    }),
  preAuthCount: (status: PreAuthStatus) =>
    queryOptions({
      queryKey: [...insuranceKeys.preAuths, 'count', status],
      queryFn: async ({ signal }) => (await api.get<PagedResult<PreAuthListItem>>('/api/v1/insurance/preauths', { query: { status, pageSize: 1 }, signal })).totalCount,
      refetchInterval: 120_000,
    }),
  claim: (id: string) =>
    queryOptions({
      queryKey: [...insuranceKeys.claims, 'detail', id],
      queryFn: ({ signal }) => api.get<ClaimDetail>(`/api/v1/insurance/claims/${id}`, { signal }),
    }),
  ageing: (asOf?: string) =>
    queryOptions({
      queryKey: ['insurance', 'ageing', asOf ?? 'today'],
      queryFn: ({ signal }) => api.get<ReceivablesAgeingReport>('/api/v1/insurance/receivables/ageing', { query: { asOf }, signal }),
    }),
};

// ------------------------------------------------------------------ writes

export const insuranceApi = {
  createPayer: (request: SavePayerRequest) => api.post<PayerDetail>('/api/v1/insurance/payers', request),
  updatePayer: (id: string, request: SavePayerRequest) => api.put<PayerDetail>(`/api/v1/insurance/payers/${id}`, request),
  addNonPayable: (request: SaveNonPayableItemRequest) => api.post<{ id: string }>('/api/v1/insurance/non-payable-items', request),
  deleteNonPayable: (id: string) => api.delete<void>(`/api/v1/insurance/non-payable-items/${id}`),
  searchPolicies: (policyNumber: string) => api.get<PolicyDto[]>('/api/v1/insurance/policies/search', { query: { policyNumber } }),
  revealPolicyNumber: (id: string) => api.get<PolicyNumberDto>(`/api/v1/insurance/policies/${id}/policy-number`),
  createPolicy: (request: SavePolicyRequest) => api.post<PolicyDto>('/api/v1/insurance/policies', request),
  updatePolicy: (id: string, request: SavePolicyRequest) => api.put<void>(`/api/v1/insurance/policies/${id}`, request),
  verifyPolicy: (id: string, notes: string | null) => api.post<void>(`/api/v1/insurance/policies/${id}/verify`, { notes }),
  createPreAuth: (request: SavePreAuthRequest) => api.post<{ id: string }>('/api/v1/insurance/preauths', request),
  updatePreAuth: (id: string, request: SavePreAuthRequest) => api.put<void>(`/api/v1/insurance/preauths/${id}`, request),
  submitPreAuth: (id: string) => api.post<void>(`/api/v1/insurance/preauths/${id}/submit`),
  preAuthQuery: (id: string, text: string) => api.post<void>(`/api/v1/insurance/preauths/${id}/query`, { text }),
  preAuthAnswer: (id: string, text: string) => api.post<void>(`/api/v1/insurance/preauths/${id}/answer`, { text }),
  approvePreAuth: (id: string, approvedAmount: number, reference: string | null) =>
    api.post<void>(`/api/v1/insurance/preauths/${id}/approve`, { approvedAmount, reference }),
  rejectPreAuth: (id: string, reason: string) => api.post<void>(`/api/v1/insurance/preauths/${id}/reject`, { reason }),
  requestEnhancement: (id: string, amount: number, reason: string) => api.post<void>(`/api/v1/insurance/preauths/${id}/enhancements`, { amount, reason }),
  respondEnhancement: (id: string, enhancementId: string, approved: boolean, approvedAmount: number, reference: string | null) =>
    api.post<void>(`/api/v1/insurance/preauths/${id}/enhancements/${enhancementId}/respond`, { approved, approvedAmount, reference }),
  finalApproval: (id: string, amount: number, reference: string | null) => api.post<void>(`/api/v1/insurance/preauths/${id}/final-approval`, { amount, reference }),
  createClaim: (request: CreateClaimRequest) => api.post<{ id: string }>('/api/v1/insurance/claims', request),
  updateClaimCoding: (id: string, request: UpdateClaimCodingRequest) => api.put<void>(`/api/v1/insurance/claims/${id}/coding`, request),
  claimQuery: (id: string, text: string) => api.post<void>(`/api/v1/insurance/claims/${id}/query`, { text }),
  claimAnswer: (id: string, text: string) => api.post<void>(`/api/v1/insurance/claims/${id}/answer`, { text }),
  approveClaim: (id: string, approvedAmount: number) => api.post<void>(`/api/v1/insurance/claims/${id}/approve`, { approvedAmount }),
  settleClaim: (id: string, request: SettleClaimRequest, idempotencyKey?: string) => api.post<void>(`/api/v1/insurance/claims/${id}/settle`, request, { idempotencyKey }),
  rejectClaim: (id: string, reason: string) => api.post<void>(`/api/v1/insurance/claims/${id}/reject`, { reason }),
};

/** Uploads a supporting document (discharge summary, investigation report) to the encrypted file store. */
export async function uploadInsuranceDocument(file: File, patientId: string): Promise<string> {
  const data = new FormData();
  data.append('file', file);
  data.append('category', 'insurance-document');
  data.append('patientId', patientId);
  const stored = await api.post<{ id: string }>('/api/v1/files', data, { timeoutMs: 120_000 });
  return stored.id;
}
