import type { Tone } from '@/components/ListRow';
import type { MessageKey } from '@/i18n';
import type { BillStatus, BillType, ReceiptPurpose } from '@/lib/types';

export const BILL_STATUS: Record<BillStatus, { key: MessageKey; tone: Tone }> = {
  Draft: { key: 'bills.status.Draft', tone: 'neutral' },
  Finalized: { key: 'bills.status.Finalized', tone: 'warning' },
  PartiallyPaid: { key: 'bills.status.PartiallyPaid', tone: 'warning' },
  Paid: { key: 'bills.status.Paid', tone: 'success' },
  Cancelled: { key: 'bills.status.Cancelled', tone: 'neutral' },
  Refunded: { key: 'bills.status.Refunded', tone: 'neutral' },
};

export const BILL_TYPE: Record<BillType, MessageKey> = {
  OutPatient: 'bills.type.OutPatient',
  InPatient: 'bills.type.InPatient',
};

export const RECEIPT_PURPOSE: Record<ReceiptPurpose, MessageKey> = {
  BillPayment: 'bills.receipt.purpose.BillPayment',
  Deposit: 'bills.receipt.purpose.Deposit',
  Advance: 'bills.receipt.purpose.Advance',
  PayerSettlement: 'bills.receipt.purpose.PayerSettlement',
};

/** Money is compared in paise so ₹0.004 rounding noise never shows a bill as "due". */
export function isDue(amount: number): boolean {
  return Math.round(amount * 100) > 0;
}

/** Total still to pay across bills (in rupees, rounded to paise). */
export function totalDue(bills: readonly { dueAmount: number }[]): number {
  return bills.reduce((sum, bill) => sum + Math.max(0, Math.round(bill.dueAmount * 100)), 0) / 100;
}
