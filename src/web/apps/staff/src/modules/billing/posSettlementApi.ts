import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Card and UPI terminal reconciliation: /api/v1/billing/pos/settlements (INT-POS). The acquirer's settlement reports,
 * imported by an accountant and matched with HC360's card and UPI receipts and refunds; what does not match, the
 * receipts not settled yet, and the payouts by day. Permission `billing.gateway.reconcile`.
 */

export type PosSettlementLineType = 'Sale' | 'Refund' | 'Adjustment';
export type PosSettlementMatch = 'Matched' | 'AmountDiffers' | 'NoReceipt' | 'NoRefund' | 'Review' | 'OtherCounter';
export type PosMatchBasis = 'Rrn' | 'ApprovalCode' | 'CardAndAmount';
export type PosPaymentMode = 'Card' | 'Upi';

/** The ones an accountant looks at, worst first. */
export const ATTENTION_MATCHES = ['NoReceipt', 'AmountDiffers', 'NoRefund', 'Review'] as const;

export const POS_MATCH_LABEL: Record<PosSettlementMatch, string> = {
  Matched: 'Matched',
  AmountDiffers: 'Amount differs',
  NoReceipt: 'Settled, no receipt',
  NoRefund: 'Refund not in HC360',
  Review: 'Chargeback',
  OtherCounter: 'Another counter',
};

/** What to do about each, in one sentence. */
export const POS_MATCH_HELP: Record<PosSettlementMatch, string> = {
  Matched: 'HC360 records this payment.',
  AmountDiffers: 'HC360 records another amount for this payment. Check the receipt and the charge slip.',
  NoReceipt:
    'The acquirer paid this money, but no receipt records it. Record the payment on its bill, then match again.',
  NoRefund: 'The acquirer took this money back as a refund, but no HC360 refund records it.',
  Review: 'The acquirer took money back (a chargeback or correction). Check it with the acquirer.',
  OtherCounter: 'Taken at a terminal of a counter HC360 billing does not record, such as the pharmacy.',
};

export const POS_MATCH_BASIS_LABEL: Record<PosMatchBasis, string> = {
  Rrn: 'by the RRN',
  ApprovalCode: 'by the approval code and amount',
  CardAndAmount: "by the card's last digits and amount",
};

export const POS_LINE_TYPE_LABEL: Record<PosSettlementLineType, string> = {
  Sale: 'Payment',
  Refund: 'Refund',
  Adjustment: 'Chargeback',
};

export interface PosSettlementImportDto {
  id: string;
  acquirer: string;
  fileName: string;
  fileSizeBytes: number;
  rows: number;
  added: number;
  alreadyImported: number;
  ignored: number;
  firstSettledOn: string | null;
  lastSettledOn: string | null;
  importedAt: string;
  importedByName: string | null;
}

export interface PosSettlementImportResult {
  import: PosSettlementImportDto;
  /** The headings HC360 read, as `Amount from "Txn Amount"`. */
  columns: string[];
  /** Rows left out and why (the first 20). */
  problems: string[];
  matched: number;
  toLookAt: number;
  message: string;
}

export interface PosSettlementLineDto {
  id: string;
  acquirer: string;
  type: PosSettlementLineType;
  mode: PosPaymentMode;
  terminalId: string | null;
  transactionAt: string | null;
  transactionOn: string | null;
  amount: number;
  charges: number;
  tax: number;
  net: number;
  rrn: string | null;
  approvalCode: string | null;
  cardLast4: string | null;
  settledOn: string;
  utr: string | null;
  match: PosSettlementMatch;
  matchBasis: PosMatchBasis | null;
  recordedAmount: number | null;
  receiptId: string | null;
  receiptNumber: string | null;
  billId: string | null;
  refundId: string | null;
  refundNumber: string | null;
  needsAttention: boolean;
  reviewedAt: string | null;
  reviewedByName: string | null;
  reviewNote: string | null;
  version: number;
}

/** One payout: a day's transactions under one UTR, which is one credit on the bank statement and one journal. */
export interface PosPayoutDto {
  settledOn: string;
  utr: string | null;
  acquirers: string;
  lines: number;
  sales: number;
  refunds: number;
  charges: number;
  tax: number;
  net: number;
  toLookAt: number;
  voucherNumber: string;
}

export interface PosSettlementDayDto {
  date: string;
  lines: number;
  sales: number;
  refunds: number;
  charges: number;
  tax: number;
  net: number;
  toLookAt: number;
  payouts: PosPayoutDto[];
}

export interface UnsettledPosReceiptDto {
  receiptId: string;
  receiptNumber: string;
  paymentId: string;
  billId: string | null;
  receivedAt: string;
  mode: PosPaymentMode;
  amount: number;
  reference: string | null;
  cardLast4: string | null;
  terminalId: string | null;
  daysWaiting: number;
}

export interface PosSettlementCountDto {
  match: PosSettlementMatch;
  count: number;
  amount: number;
}

export interface PosSettlementSummaryDto {
  settlementDays: number;
  toLookAt: PosSettlementCountDto[];
  unsettled: number;
  unsettledAmount: number;
  lastSettledOn: string | null;
  lastImport: PosSettlementImportDto | null;
  otherTerminals: string[];
}

export interface PosMatchResult {
  checked: number;
  matched: number;
  toLookAt: number;
  message: string;
}

/** A payout's transactions, or the ones to look at. */
export interface PosLineFilter {
  attention?: boolean;
  settledOn?: string;
  utr?: string | null;
  page?: number;
}

const BASE = '/api/v1/billing/pos/settlements';

export const posSettlementKeys = {
  all: ['billing', 'pos-settlements'] as const,
  summary: () => [...posSettlementKeys.all, 'summary'] as const,
  lines: () => [...posSettlementKeys.all, 'lines'] as const,
  days: () => [...posSettlementKeys.all, 'days'] as const,
  unsettled: () => [...posSettlementKeys.all, 'unsettled'] as const,
  imports: () => [...posSettlementKeys.all, 'imports'] as const,
};

/** The query of a line filter: a payout without a UTR is asked for with `noUtr`. */
export function lineQuery(filter: PosLineFilter): Record<string, string | number | boolean | undefined> {
  return {
    pageSize: 25,
    page: filter.page,
    attention: filter.attention || undefined,
    settledOn: filter.settledOn,
    utr: filter.settledOn && filter.utr ? filter.utr : undefined,
    noUtr: filter.settledOn && filter.utr === null ? true : undefined,
  };
}

export const posSettlementQueries = {
  summary: () =>
    queryOptions({
      queryKey: posSettlementKeys.summary(),
      queryFn: ({ signal }) => api.get<PosSettlementSummaryDto>(`${BASE}/summary`, { signal }),
    }),
  lines: (filter: PosLineFilter) =>
    queryOptions({
      queryKey: [...posSettlementKeys.lines(), filter],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PosSettlementLineDto>>(`${BASE}/lines`, { query: lineQuery(filter), signal }),
      placeholderData: keepPreviousData,
    }),
  days: (from: string, to: string) =>
    queryOptions({
      queryKey: [...posSettlementKeys.days(), from, to],
      queryFn: ({ signal }) => api.get<PosSettlementDayDto[]>(`${BASE}/days`, { query: { from, to }, signal }),
      placeholderData: keepPreviousData,
    }),
  unsettled: () =>
    queryOptions({
      queryKey: posSettlementKeys.unsettled(),
      queryFn: ({ signal }) => api.get<UnsettledPosReceiptDto[]>(`${BASE}/unsettled`, { signal }),
    }),
  imports: (page: number) =>
    queryOptions({
      queryKey: [...posSettlementKeys.imports(), page],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PosSettlementImportDto>>(`${BASE}/imports`, { query: { page, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
};

export const posSettlementApi = {
  /**
   * Uploads the acquirer's settlement report (CSV or .xlsx). `settledOn` is needed only for a report without a
   * settlement date column.
   */
  importReport: (file: File, acquirer: string, settledOn: string | null) => {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('acquirer', acquirer);
    if (settledOn) {
      form.append('settledOn', settledOn);
    }
    return api.post<PosSettlementImportResult>(`${BASE}/imports`, form, { timeoutMs: 5 * 60_000 });
  },
  review: (id: string, note: string, version: number) =>
    api.post<PosSettlementLineDto>(`${BASE}/lines/${id}/review`, { note, version }),
  match: () => api.post<PosMatchResult>(`${BASE}/match`, undefined, { timeoutMs: 120_000 }),
  trainingFile: (date: string) => api.download(`${BASE}/training-file`, { query: { date } }),
};

/** The reasons in a few words each, for "2 without a receipt, 1 chargeback". */
const REASON_WORDS: Record<(typeof ATTENTION_MATCHES)[number], [one: string, many: string]> = {
  NoReceipt: ['without a receipt', 'without a receipt'],
  AmountDiffers: ['with another amount', 'with another amount'],
  NoRefund: ['refund not in HC360', 'refunds not in HC360'],
  Review: ['chargeback', 'chargebacks'],
};

/** What there is to look at, by reason: "2 without a receipt, 1 chargeback". */
export function toLookAtHint(summary: PosSettlementSummaryDto | undefined): string {
  return (summary?.toLookAt ?? [])
    .filter((reason) => reason.match in REASON_WORDS && reason.count > 0)
    .map((reason) => {
      const [one, many] = REASON_WORDS[reason.match as keyof typeof REASON_WORDS];
      return `${reason.count} ${reason.count === 1 ? one : many}`;
    })
    .join(', ');
}

/** How many transactions an accountant has to look at, across the reasons. */
export function toLookAtCount(summary: PosSettlementSummaryDto | undefined): number {
  return (summary?.toLookAt ?? []).reduce((total, reason) => total + reason.count, 0);
}

/** The references of a settled transaction, as the bank statement and the charge slip show them. */
export function references(
  line: Pick<PosSettlementLineDto, 'rrn' | 'approvalCode' | 'cardLast4' | 'terminalId'>,
): string {
  return [
    line.rrn && `RRN ${line.rrn}`,
    line.approvalCode && `Approval ${line.approvalCode}`,
    line.cardLast4 && `Card ending ${line.cardLast4}`,
    line.terminalId && `TID ${line.terminalId}`,
  ]
    .filter(Boolean)
    .join(' · ');
}

/** What a line matched, in words: the receipt or refund and how it was found, or why it did not match. */
export function matchText(
  line: Pick<PosSettlementLineDto, 'match' | 'matchBasis' | 'receiptNumber' | 'refundNumber' | 'recordedAmount'>,
): string {
  const document = line.receiptNumber
    ? `Receipt ${line.receiptNumber}`
    : line.refundNumber
      ? `Refund ${line.refundNumber}`
      : null;
  const basis = line.matchBasis ? ` (${POS_MATCH_BASIS_LABEL[line.matchBasis]})` : '';
  switch (line.match) {
    case 'Matched':
      return `${document ?? 'Matched'}${basis}`;
    case 'AmountDiffers':
      return `${document ?? 'A receipt'}${basis} records another amount`;
    default:
      return POS_MATCH_HELP[line.match];
  }
}
