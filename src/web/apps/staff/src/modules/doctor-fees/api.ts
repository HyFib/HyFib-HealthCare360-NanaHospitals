import type { PagedResult, PayerType } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { PaymentMode, ServiceCategory } from '@/modules/billing/labels';

/*
 * Doctor fee accounting (/api/v1/doctor-fees, Billing module Features/DoctorFees, DFA-01..05): revenue-share rules,
 * accruals per bill line, payouts with TDS and deductions, and statements.
 */

export const SHARE_METHODS = ['Percentage', 'FixedPerUnit'] as const;
export type ShareMethod = (typeof SHARE_METHODS)[number];
export const SHARE_METHOD_LABEL: Record<ShareMethod, string> = {
  Percentage: '% of the billed value',
  FixedPerUnit: 'Fixed ₹ per unit',
};

export const ACCRUAL_STATUSES = ['Accrued', 'InPayout', 'Paid', 'Reversed'] as const;
export type AccrualStatus = (typeof ACCRUAL_STATUSES)[number];
export const ACCRUAL_STATUS_LABEL: Record<AccrualStatus, string> = {
  Accrued: 'Unpaid',
  InPayout: 'In a payout',
  Paid: 'Paid',
  Reversed: 'Reversed',
};

export const PAYOUT_STATUSES = ['Draft', 'Approved', 'Paid'] as const;
export type PayoutStatus = (typeof PAYOUT_STATUSES)[number];
export const PAYOUT_STATUS_LABEL: Record<PayoutStatus, string> = {
  Draft: 'Awaiting approval',
  Approved: 'Approved, to pay',
  Paid: 'Paid',
};

/** Ways a doctor is paid (PayPayoutRequest; advances and insurance are not payout modes). */
export const PAYOUT_MODES = ['NetBanking', 'Cheque', 'Upi', 'Cash'] as const satisfies readonly PaymentMode[];
export const PAYOUT_MODE_LABEL: Record<(typeof PAYOUT_MODES)[number], string> = {
  NetBanking: 'Bank transfer (NEFT/RTGS/IMPS)',
  Cheque: 'Cheque',
  Upi: 'UPI',
  Cash: 'Cash',
};

export interface ShareRuleDto {
  id: string;
  practitionerId: string | null;
  practitionerName: string | null;
  serviceItemId: string | null;
  serviceCode: string | null;
  category: ServiceCategory | null;
  payerType: PayerType | null;
  method: ShareMethod;
  value: number;
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
  notes: string | null;
  version: number;
}

export interface SaveShareRuleRequest {
  practitionerId: string | null;
  serviceItemId: string | null;
  category: ServiceCategory | null;
  payerType: PayerType | null;
  method: ShareMethod;
  value: number;
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
  notes: string | null;
  version: number | null;
}

export interface AccrualDto {
  id: string;
  practitionerId: string;
  practitionerName: string | null;
  billId: string;
  billNumber: string;
  serviceCode: string;
  description: string;
  category: ServiceCategory;
  payerType: PayerType;
  serviceDate: string;
  accruedOn: string;
  basisAmount: number;
  method: ShareMethod;
  rate: number;
  shareAmount: number;
  status: AccrualStatus;
  payoutId: string | null;
  isReversal: boolean;
}

export interface PayoutDeductionDto {
  id: string;
  description: string;
  amount: number;
}

export interface PayoutDto {
  id: string;
  payoutNumber: string;
  practitionerId: string;
  practitionerName: string | null;
  periodFrom: string;
  periodTo: string;
  grossAmount: number;
  deductionAmount: number;
  tdsPercent: number;
  tdsAmount: number;
  netAmount: number;
  status: PayoutStatus;
  approvedBy: string | null;
  approvedAt: string | null;
  paidAt: string | null;
  paidReference: string | null;
  paymentMode: PaymentMode | null;
  notes: string | null;
  accrualCount: number;
  deductions: PayoutDeductionDto[];
  version: number;
}

export interface CreatePayoutRequest {
  practitionerId: string;
  periodFrom: string;
  periodTo: string;
  deductions: { description: string; amount: number }[] | null;
  tdsPercent: number | null;
  notes: string | null;
}

export interface DoctorStatement {
  practitionerId: string;
  practitionerName: string | null;
  from: string;
  to: string;
  accrued: number;
  reversed: number;
  inPayout: number;
  paidOut: number;
  unpaid: number;
  accruals: AccrualDto[];
  payouts: PayoutDto[];
}

export interface AccrualFilters {
  practitionerId?: string;
  from?: string;
  to?: string;
  status?: AccrualStatus;
  page?: number;
}

export interface PayoutFilters {
  practitionerId?: string;
  status?: PayoutStatus;
  page?: number;
}

export const feeKeys = {
  all: ['doctor-fees'] as const,
  rules: ['doctor-fees', 'rules'] as const,
  accruals: ['doctor-fees', 'accruals'] as const,
  payouts: ['doctor-fees', 'payouts'] as const,
  statements: ['doctor-fees', 'statements'] as const,
};

export const feeQueries = {
  rules: (practitionerId?: string, includeInactive = false) =>
    queryOptions({
      queryKey: [...feeKeys.rules, practitionerId ?? 'all', includeInactive],
      queryFn: ({ signal }) =>
        api.get<ShareRuleDto[]>('/api/v1/doctor-fees/rules', { query: { practitionerId, includeInactive }, signal }),
    }),
  accruals: (filters: AccrualFilters) =>
    queryOptions({
      queryKey: [...feeKeys.accruals, filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<AccrualDto>>('/api/v1/doctor-fees/accruals', {
          query: { pageSize: 25, ...filters },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  payouts: (filters: PayoutFilters) =>
    queryOptions({
      queryKey: [...feeKeys.payouts, 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PayoutDto>>('/api/v1/doctor-fees/payouts', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  payout: (id: string) =>
    queryOptions({
      queryKey: [...feeKeys.payouts, 'detail', id],
      queryFn: ({ signal }) => api.get<PayoutDto>(`/api/v1/doctor-fees/payouts/${id}`, { signal }),
    }),
  payoutCount: (status: PayoutStatus) =>
    queryOptions({
      queryKey: [...feeKeys.payouts, 'count', status],
      queryFn: async ({ signal }) =>
        (
          await api.get<PagedResult<PayoutDto>>('/api/v1/doctor-fees/payouts', {
            query: { status, pageSize: 1 },
            signal,
          })
        ).totalCount,
    }),
  statement: (practitionerId: string, from: string, to: string) =>
    queryOptions({
      queryKey: [...feeKeys.statements, practitionerId, from, to],
      queryFn: ({ signal }) =>
        api.get<DoctorStatement>('/api/v1/doctor-fees/statements', { query: { practitionerId, from, to }, signal }),
    }),
};

export const feeApi = {
  createRule: (request: SaveShareRuleRequest) => api.post<{ id: string }>('/api/v1/doctor-fees/rules', request),
  updateRule: (id: string, request: SaveShareRuleRequest) => api.put<void>(`/api/v1/doctor-fees/rules/${id}`, request),
  deleteRule: (id: string) => api.delete<void>(`/api/v1/doctor-fees/rules/${id}`),
  createPayout: (request: CreatePayoutRequest) => api.post<{ id: string }>('/api/v1/doctor-fees/payouts', request),
  approvePayout: (id: string, version: number) =>
    api.post<void>(`/api/v1/doctor-fees/payouts/${id}/approve`, { version }),
  payPayout: (id: string, mode: PaymentMode, reference: string | null, version: number, idempotencyKey?: string) =>
    api.post<void>(`/api/v1/doctor-fees/payouts/${id}/pay`, { mode, reference, version }, { idempotencyKey }),
  discardPayout: (id: string, version: number) =>
    api.delete<void>(`/api/v1/doctor-fees/payouts/${id}`, { query: { version } }),
};
