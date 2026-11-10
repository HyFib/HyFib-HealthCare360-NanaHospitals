import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import type { PaymentMode } from './labels';

/**
 * Chargebacks on card, UPI and gateway payments: /api/v1/billing/disputes. A case is opened by HC360 when a settlement
 * takes money back on a payment it recorded or the gateway reports a dispute, or by an accountant from the bank's
 * notice. A gateway payment's case follows the gateway's own record of the dispute (its reason code, phase, status and
 * deadline). Accountants record what the notice says, the evidence sent and a win; accepting the loss asks for the
 * receipt to be reversed, which a second person who approves reversals makes. Permission `billing.gateway.reconcile`.
 */

export type PaymentDisputeSource = 'Gateway' | 'Terminal' | 'Notice';
export type PaymentDisputeStatus = 'Open' | 'Contested' | 'Won' | 'Lost';

export const DISPUTE_STATUS_LABEL: Record<PaymentDisputeStatus, string> = {
  Open: 'To answer',
  Contested: 'Evidence sent',
  Won: 'Won',
  Lost: 'Lost',
};

export const DISPUTE_SOURCE_LABEL: Record<PaymentDisputeSource, string> = {
  Gateway: 'From the gateway',
  Terminal: "From the acquirer's report",
  Notice: "From the bank's notice",
};

export interface PaymentDisputeDto {
  id: string;
  number: string;
  source: PaymentDisputeSource;
  status: PaymentDisputeStatus;
  receiptId: string;
  receiptNumber: string;
  receiptPaymentId: string;
  billId: string | null;
  mode: string;
  /** The gateway's payment id, or the terminal transaction's RRN. */
  paymentReference: string | null;
  amount: number;
  reasonCode: string | null;
  reason: string | null;
  respondBy: string | null;
  /**
   * Days until the answer is due (negative when late); null without a deadline, once answered or closed, or once the
   * gateway decided against the hospital.
   */
  daysLeft: number | null;
  openedAt: string;
  openedByName: string | null;
  evidenceSentOn: string | null;
  evidenceNote: string | null;
  evidenceByName: string | null;
  lossAskedAt: string | null;
  lossAskedByName: string | null;
  /** Accepting the loss was asked for and waits for a second person. */
  lossWaiting: boolean;
  closedAt: string | null;
  closedByName: string | null;
  outcome: string | null;
  reversalNumber: string | null;
  /** The gateway's own record of the dispute the case follows: its reason code, phase, status and deadline. */
  gatewayDisputeId: string | null;
  /** The gateway's word for where the dispute stands: open, under_review, won, lost, closed… */
  gatewayStatus: string | null;
  /** chargeback, pre_arbitration, arbitration… */
  gatewayPhase: string | null;
  /** The gateway decided against the hospital and the loss is not booked yet: accept it. */
  decidedAgainst: boolean;
  version: number;
}

export interface PaymentDisputeSummaryDto {
  open: number;
  openAmount: number;
  dueSoon: number;
  late: number;
  /** Cases the gateway decided against the hospital, whose loss is still to be accepted. */
  decidedAgainst: number;
}

/** A receipt's card, UPI or online payment line, to choose the one the bank took back. */
export interface DisputablePaymentDto {
  id: string;
  mode: PaymentMode;
  amount: number;
  /** The gateway's payment id, or the RRN or reference the cashier recorded. */
  reference: string | null;
  /** A chargeback on this line is open already. */
  hasOpenCase: boolean;
}

export interface OpenPaymentDisputeRequest {
  receiptNumber: string;
  paymentId: string | null;
  amount: number;
  reasonCode: string | null;
  reason: string | null;
  respondBy: string | null;
}

/** Which cases: open or closed, and of the card and UPI terminals or of the gateway. */
export interface DisputeFilter {
  open: boolean;
  terminal: boolean;
  page?: number;
}

const BASE = '/api/v1/billing/disputes';

export const disputeKeys = {
  all: ['billing', 'disputes'] as const,
  list: () => [...disputeKeys.all, 'list'] as const,
  summary: () => [...disputeKeys.all, 'summary'] as const,
};

export const disputeQueries = {
  list: (filter: DisputeFilter) =>
    queryOptions({
      queryKey: [...disputeKeys.list(), filter],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PaymentDisputeDto>>(BASE, {
          query: { open: filter.open, terminal: filter.terminal, page: filter.page, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  summary: () =>
    queryOptions({
      queryKey: disputeKeys.summary(),
      queryFn: ({ signal }) => api.get<PaymentDisputeSummaryDto>(`${BASE}/summary`, { signal }),
    }),
  /** The card, UPI and online payment lines of the receipt a notice names. */
  paymentLines: (receiptNumber: string) =>
    queryOptions({
      queryKey: [...disputeKeys.all, 'payment-lines', receiptNumber],
      queryFn: ({ signal }) =>
        api.get<DisputablePaymentDto[]>(`${BASE}/payment-lines`, { query: { receiptNumber }, signal }),
      retry: false,
    }),
};

export const disputeApi = {
  open: (body: OpenPaymentDisputeRequest) => api.post<PaymentDisputeDto>(BASE, body),
  describe: (
    id: string,
    body: { reasonCode: string | null; reason: string | null; respondBy: string | null; version: number },
  ) => api.post<PaymentDisputeDto>(`${BASE}/${id}/details`, body),
  evidence: (id: string, body: { sentOn: string; note: string; version: number }) =>
    api.post<PaymentDisputeDto>(`${BASE}/${id}/evidence`, body),
  won: (id: string, note: string, version: number) =>
    api.post<PaymentDisputeDto>(`${BASE}/${id}/won`, { note, version }),
  /** Asks to accept the loss: the receipt is reversed when someone else who approves reversals approves it. */
  acceptLoss: (id: string, note: string, version: number) =>
    api.post<ApprovalRequestDto>(`${BASE}/${id}/accept-loss`, { note, version }),
};

/** The deadline in words: "Answer by 12 Nov (3 days left)", "Late by 2 days", "Due today". */
export function deadlineText(
  dispute: Pick<PaymentDisputeDto, 'respondBy' | 'daysLeft'> & Partial<Pick<PaymentDisputeDto, 'decidedAgainst'>>,
  format: (iso: string) => string,
): string | null {
  if (!dispute.respondBy || dispute.decidedAgainst) {
    return null;
  }

  if (dispute.daysLeft === null) {
    return `Answer was due by ${format(dispute.respondBy)}`;
  }

  if (dispute.daysLeft < 0) {
    const late = -dispute.daysLeft;
    return `Late by ${late} day${late === 1 ? '' : 's'} (due ${format(dispute.respondBy)})`;
  }

  return dispute.daysLeft === 0
    ? 'Answer due today'
    : `Answer by ${format(dispute.respondBy)} (${dispute.daysLeft} day${dispute.daysLeft === 1 ? '' : 's'} left)`;
}

/** What is next for a case, in a few words. */
export function nextStep(
  dispute: Pick<PaymentDisputeDto, 'status' | 'lossWaiting' | 'respondBy'> &
    Partial<Pick<PaymentDisputeDto, 'decidedAgainst'>>,
): string {
  if (dispute.status === 'Won' || dispute.status === 'Lost') {
    return dispute.status === 'Won' ? 'Closed: the money came back' : 'Closed: the receipt was reversed';
  }

  if (dispute.lossWaiting) {
    return 'Accepting the loss waits for a second person';
  }

  if (dispute.decidedAgainst) {
    return 'The bank decided against the hospital: accept the loss';
  }

  if (dispute.status === 'Contested') {
    return 'Waiting for the bank to decide';
  }

  return dispute.respondBy ? 'Send the evidence, or accept the loss' : "Record the bank's notice and its deadline";
}

/** The gateway's own words for a dispute (dispute id, phase, status), or null for a case that follows none. */
export function gatewayText(
  dispute: Pick<PaymentDisputeDto, 'gatewayDisputeId' | 'gatewayPhase' | 'gatewayStatus'>,
): string | null {
  if (!dispute.gatewayDisputeId) {
    return null;
  }

  const words = (word: string) => word.replaceAll('_', ' ');
  return [
    `Dispute ${dispute.gatewayDisputeId}`,
    dispute.gatewayPhase && words(dispute.gatewayPhase),
    dispute.gatewayStatus && `gateway: ${words(dispute.gatewayStatus)}`,
  ]
    .filter(Boolean)
    .join(' · ');
}
