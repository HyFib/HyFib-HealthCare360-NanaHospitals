import type { ReceiptPaymentDto, RefundDto } from './api';
import { REVERSIBLE_MODES, type ReceiptPurpose } from './labels';

/** What is left of a payment line to take back. */
export const remaining = (line: ReceiptPaymentDto) => line.amount - (line.reversed ?? 0);

/** Lines whose money a reversal can take back: not cash, an advance or an insurer's, and not taken back already. */
export const reversibleLines = (payments: ReceiptPaymentDto[]) =>
  payments.filter((p) => p.id && REVERSIBLE_MODES.has(p.mode) && remaining(p) > 0);

/**
 * Whether a receipt may hold money a reversal could take back, before its lines are loaded: one of its modes ("Cash +
 * Upi") is one a reversal takes back, not all of it is taken back yet, and it is not an insurer's settlement (corrected
 * with the claim, never reversed).
 */
export const mayHaveReversibleMoney = (receipt: {
  purpose: ReceiptPurpose;
  modes: string;
  amount: number;
  reversedAmount?: number;
}) =>
  receipt.purpose !== 'PayerSettlement' &&
  (receipt.reversedAmount ?? 0) < receipt.amount &&
  receipt.modes.split(' + ').some((m) => REVERSIBLE_MODES.has(m as never));

/** Whether a paid refund may be recorded as not paid out: money was recorded as leaving, in a mode not counted at the drawer. */
export const mayReverseRefund = (refund: RefundDto) =>
  refund.status === 'Paid' &&
  !refund.notPaidOut &&
  refund.mode != null &&
  refund.mode !== 'Cash' &&
  refund.mode !== 'Advance';
