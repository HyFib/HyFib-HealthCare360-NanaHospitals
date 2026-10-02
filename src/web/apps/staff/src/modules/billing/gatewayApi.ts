import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Gateway reconciliation: /api/v1/billing/gateway (INT-PG). The payment gateway's settlements by day, matched to
 * receipts and refunds, and the exceptions an accountant deals with. Permission `billing.gateway.reconcile`.
 */

export const DISCREPANCY_KINDS = ['UnrecordedPayment', 'UnsettledReceipt', 'AmountMismatch', 'RefundMismatch'] as const;
export type DiscrepancyKind = (typeof DISCREPANCY_KINDS)[number];
export type DiscrepancyStatus = 'Open' | 'Resolved';
export type SettlementStatus = 'Created' | 'Processed' | 'Failed';
export type TransactionType = 'Payment' | 'Refund' | 'Adjustment' | 'Transfer' | 'Other';
export type SettlementMatch = 'Matched' | 'AmountDiffers' | 'NoReceipt' | 'NoRefund' | 'NotApplicable';

/** Worst first, in the order the list shows them. */
export const DISCREPANCY_KIND_LABEL: Record<DiscrepancyKind, string> = {
  UnrecordedPayment: 'Paid, no receipt',
  UnsettledReceipt: 'Not settled',
  AmountMismatch: 'Amounts differ',
  RefundMismatch: 'Refund differs',
};

/** What to do about each kind, in one sentence. */
export const DISCREPANCY_KIND_HELP: Record<DiscrepancyKind, string> = {
  UnrecordedPayment: 'The patient paid online but was not credited. Record the receipt.',
  UnsettledReceipt:
    'HC360 has the receipt, but the gateway has not paid it to the bank. Check again, then ask the gateway.',
  AmountMismatch: 'The gateway and HC360 record different amounts. Check the receipt and the bank statement.',
  RefundMismatch: 'The gateway reports a refund differently. A failed refund can be sent again.',
};

export const SETTLEMENT_MATCH_LABEL: Record<SettlementMatch, string> = {
  Matched: 'Matched',
  AmountDiffers: 'Amount differs',
  NoReceipt: 'No receipt',
  NoRefund: 'No refund',
  NotApplicable: 'Gateway adjustment',
};

export const TRANSACTION_TYPE_LABEL: Record<TransactionType, string> = {
  Payment: 'Payment',
  Refund: 'Refund',
  Adjustment: 'Adjustment',
  Transfer: 'Transfer',
  Other: 'Other',
};

export const SETTLEMENT_STATUS_LABEL: Record<SettlementStatus, string> = {
  Created: 'Not yet paid to the bank',
  Processed: 'Paid to the bank',
  Failed: 'Bank transfer failed',
};

export interface GatewaySettlementDto {
  id: string;
  provider: string;
  settlementId: string;
  utr: string | null;
  status: SettlementStatus;
  settledAt: string;
  settledOn: string;
  paymentsAmount: number;
  refundsAmount: number;
  adjustmentsAmount: number;
  charges: number;
  tax: number;
  amount: number;
  difference: number;
  lines: number;
  unmatched: number;
}

export interface SettlementDayDto {
  date: string;
  count: number;
  paymentsAmount: number;
  refundsAmount: number;
  adjustmentsAmount: number;
  charges: number;
  tax: number;
  amount: number;
  unmatched: number;
  settlements: GatewaySettlementDto[];
}

export interface SettlementLineDto {
  id: string;
  type: TransactionType;
  entityId: string;
  paymentId: string | null;
  orderId: string | null;
  method: string | null;
  transactionAt: string | null;
  amount: number;
  fee: number;
  tax: number;
  net: number;
  match: SettlementMatch;
  recordedAmount: number | null;
  receiptId: string | null;
  receiptNumber: string | null;
  refundId: string | null;
  refundNumber: string | null;
}

export interface GatewaySettlementDetail {
  settlement: GatewaySettlementDto;
  lines: SettlementLineDto[];
}

export interface SettlementFetchResult {
  date: string;
  settlements: number;
  added: number;
  updated: number;
  message: string;
}

export interface GatewayDiscrepancyDto {
  id: string;
  kind: DiscrepancyKind;
  rank: number;
  status: DiscrepancyStatus;
  key: string;
  orderId: string | null;
  summary: string;
  recordedAmount: number | null;
  gatewayAmount: number | null;
  gatewayAt: string | null;
  billId: string | null;
  billNumber: string | null;
  ipAccountId: string | null;
  receiptId: string | null;
  receiptNumber: string | null;
  refundId: string | null;
  refundNumber: string | null;
  appointmentId: string | null;
  appointmentNumber: string | null;
  gatewayOrderRecordId: string | null;
  canRecordReceipt: boolean;
  detectedAt: string;
  lastSeenAt: string;
  resolvedAt: string | null;
  resolvedByName: string | null;
  resolution: string | null;
  resolvedAutomatically: boolean;
  version: number;
}

export interface DiscrepancyActionResult {
  message: string;
  discrepancy: GatewayDiscrepancyDto;
}

export interface DiscrepancyCountDto {
  kind: DiscrepancyKind;
  count: number;
  amount: number;
}

export interface ReconciliationSummaryDto {
  enabled: boolean;
  settlementDays: number;
  open: DiscrepancyCountDto[];
  lastSettledOn: string | null;
  lastFetchedAt: string | null;
}

export interface ExceptionFilters {
  status?: DiscrepancyStatus;
  kind?: DiscrepancyKind;
  page?: number;
}

const BASE = '/api/v1/billing/gateway';

export const gatewayKeys = {
  all: ['billing', 'gateway'] as const,
  summary: () => [...gatewayKeys.all, 'summary'] as const,
  exceptions: () => [...gatewayKeys.all, 'exceptions'] as const,
  settlements: () => [...gatewayKeys.all, 'settlements'] as const,
};

export const gatewayQueries = {
  summary: () =>
    queryOptions({
      queryKey: gatewayKeys.summary(),
      queryFn: ({ signal }) => api.get<ReconciliationSummaryDto>(`${BASE}/summary`, { signal }),
    }),
  exceptions: (filters: ExceptionFilters) =>
    queryOptions({
      queryKey: [...gatewayKeys.exceptions(), filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<GatewayDiscrepancyDto>>(`${BASE}/exceptions`, {
          query: { pageSize: 25, status: filters.status, kind: filters.kind, page: filters.page },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  settlements: (from: string, to: string) =>
    queryOptions({
      queryKey: [...gatewayKeys.settlements(), from, to],
      queryFn: ({ signal }) => api.get<SettlementDayDto[]>(`${BASE}/settlements`, { query: { from, to }, signal }),
      placeholderData: keepPreviousData,
    }),
  settlement: (id: string) =>
    queryOptions({
      queryKey: [...gatewayKeys.settlements(), 'detail', id],
      queryFn: ({ signal }) => api.get<GatewaySettlementDetail>(`${BASE}/settlements/${id}`, { signal }),
    }),
};

export const gatewayApi = {
  /** Reads one day's settlements from the gateway now. */
  fetch: (date: string) =>
    api.post<SettlementFetchResult>(`${BASE}/settlements/fetch`, { date }, { timeoutMs: 120_000 }),
  recordReceipt: (id: string) => api.post<DiscrepancyActionResult>(`${BASE}/exceptions/${id}/record-receipt`),
  retry: (id: string) =>
    api.post<DiscrepancyActionResult>(`${BASE}/exceptions/${id}/retry`, undefined, { timeoutMs: 120_000 }),
  resolve: (id: string, note: string, version: number) =>
    api.post<GatewayDiscrepancyDto>(`${BASE}/exceptions/${id}/resolve`, { note, version }),
};

/** The wording of "try again" for an exception: what it does differs by kind. */
export function retryLabel(discrepancy: Pick<GatewayDiscrepancyDto, 'kind' | 'refundId' | 'summary'>): string {
  switch (discrepancy.kind) {
    case 'UnrecordedPayment':
      return 'Ask the gateway again';
    case 'UnsettledReceipt':
      return 'Read the settlements again';
    case 'RefundMismatch':
      return discrepancy.refundId && discrepancy.summary.includes('failed at the gateway')
        ? 'Send the refund again'
        : 'Check again';
    default:
      return 'Check again';
  }
}

/** The amount that matters most for an exception: the gateway's, else HC360's. */
export function headlineAmount(
  discrepancy: Pick<GatewayDiscrepancyDto, 'gatewayAmount' | 'recordedAmount'>,
): number | null {
  return discrepancy.gatewayAmount ?? discrepancy.recordedAmount;
}

/** "3 to look at" style count across kinds, for the page header. */
export function openCount(summary: ReconciliationSummaryDto | undefined): number {
  return (summary?.open ?? []).reduce((total, kind) => total + kind.count, 0);
}

/** Who resolved an exception, in words. */
export function resolvedBy(
  discrepancy: Pick<GatewayDiscrepancyDto, 'resolvedAutomatically' | 'resolvedByName'>,
): string {
  return discrepancy.resolvedAutomatically ? 'HC360 (the cause went away)' : (discrepancy.resolvedByName ?? 'Someone');
}
