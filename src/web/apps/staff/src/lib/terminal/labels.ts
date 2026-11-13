/** Card/UPI terminal statuses exactly as the server sends them, with the plain words shown to staff. Pure module. */

export const POS_STATUSES = ['Initiated', 'AwaitingCustomer', 'Approved', 'Declined', 'Cancelled', 'TimedOut'] as const;
export type PosTransactionStatus = (typeof POS_STATUSES)[number];
export const POS_STATUS_LABEL: Record<PosTransactionStatus, string> = {
  Initiated: 'Sent to the terminal',
  AwaitingCustomer: 'Waiting for the patient to tap, insert or scan',
  Approved: 'Approved',
  Declined: 'Declined',
  Cancelled: 'Cancelled',
  TimedOut: 'Timed out',
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
