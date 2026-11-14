import { formatInr, formatTime } from '@hc360/core';

/** Card/UPI terminal statuses exactly as the server sends them, with the plain words shown to staff. Pure module. */

export const POS_STATUSES = [
  'Initiated',
  'AwaitingCustomer',
  'Approved',
  'Declined',
  'Cancelled',
  'TimedOut',
  'Unconfirmed',
  'Lapsed',
] as const;
export type PosTransactionStatus = (typeof POS_STATUSES)[number];
export const POS_STATUS_LABEL: Record<PosTransactionStatus, string> = {
  Initiated: 'Sent to the terminal',
  AwaitingCustomer: 'Waiting for the patient to tap, insert or scan',
  Approved: 'Approved',
  Declined: 'Declined',
  Cancelled: 'Cancelled',
  TimedOut: 'Timed out',
  Unconfirmed: 'Not confirmed: the sale may still be on the terminal',
  Lapsed: 'Never confirmed: the outcome is unknown',
};

/** The status in words; the waiting one names who pays: the patient at Billing, the customer at a counter. */
export function terminalStatusLabel(status: PosTransactionStatus, payer = 'patient'): string {
  return status === 'AwaitingCustomer' ? `Waiting for the ${payer} to tap, insert or scan` : POS_STATUS_LABEL[status];
}

const PENDING = new Set<string>(['Initiated', 'AwaitingCustomer']);

/** True while the terminal is still waiting for the customer. */
export function isTerminalSalePending(status: string): boolean {
  return PENDING.has(status);
}

/** True for a sale whose answer from the terminal was lost: it may be on the terminal, and the customer may still pay. */
export function isTerminalSaleUnconfirmed(status: string): boolean {
  return status === 'Unconfirmed';
}

/** The slice of a sale the unconfirmed guidance reads. */
export interface UnconfirmedSale {
  amount: number;
  createdAt?: string | null;
  retryAfter?: string | null;
}

/** True while an unconfirmed sale may still be on the terminal: a new sale must wait until `retryAfter`. */
export function isTerminalBusy(sale: { status: string; retryAfter?: string | null }, now: Date): boolean {
  return (
    isTerminalSaleUnconfirmed(sale.status) && (!sale.retryAfter || new Date(sale.retryAfter).getTime() > now.getTime())
  );
}

/**
 * What to do about a sale whose answer was lost, in plain words: look at the terminal, record the payment from the slip
 * if the customer paid, otherwise wait until the terminal has dropped the sale.
 */
export function unconfirmedGuidance(sale: UnconfirmedSale): string {
  const sent = sale.createdAt ? ` sent at ${formatTime(sale.createdAt)}` : '';
  const wait = sale.retryAfter ? ` wait until ${formatTime(sale.retryAfter)}` : ' wait a few minutes';
  return (
    `The terminal did not answer, so HC360 cannot tell whether the sale of ${formatInr(sale.amount)}${sent} reached it. ` +
    `It may still be on the terminal. Check the terminal's screen. If the customer paid, record the payment from the slip; otherwise${wait}, ` +
    'or confirm that the terminal shows no sale.'
  );
}

/** "Card ending 1111 · Approval A1 · RRN 123", only the parts the terminal gave. */
export function terminalDetailsLine(sale: {
  cardLast4?: string | null;
  approvalCode?: string | null;
  rrn?: string | null;
}): string {
  return [
    sale.cardLast4 ? `Card ending ${sale.cardLast4}` : null,
    sale.approvalCode ? `Approval ${sale.approvalCode}` : null,
    sale.rrn ? `RRN ${sale.rrn}` : null,
  ]
    .filter(Boolean)
    .join(' · ');
}

/** Whether an approved terminal payment waiting unused can be taken for this screen's bill. */
export function terminalSaleFits(sale: { amount: number }, maxAmount: number, exact: boolean): boolean {
  return exact ? sale.amount === maxAmount : sale.amount > 0 && sale.amount <= maxAmount;
}
