import type { Tone } from '@/components/ListRow';
import type { MessageKey, Translate } from '@/i18n';
import type { Formatters } from '@/i18n/useFormat';
import type {
  BillStatus,
  BillType,
  PortalRefund,
  PortalRefundStage,
  ReceiptPurpose,
  RefundMode,
  RefundPurpose,
} from '@/lib/types';

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

/** Where a refund stands, as a pill; done is green, waiting amber, a bank's refusal red. */
export const REFUND_STAGE: Record<PortalRefundStage, { key: MessageKey; tone: Tone }> = {
  Requested: { key: 'refunds.stage.Requested', tone: 'warning' },
  Approved: { key: 'refunds.stage.Approved', tone: 'warning' },
  NotApproved: { key: 'refunds.stage.NotApproved', tone: 'neutral' },
  PaidOut: { key: 'refunds.stage.PaidOut', tone: 'success' },
  KeptAsAdvance: { key: 'refunds.stage.KeptAsAdvance', tone: 'success' },
  WithTheBank: { key: 'refunds.stage.WithTheBank', tone: 'warning' },
  Credited: { key: 'refunds.stage.Credited', tone: 'success' },
  BankReturned: { key: 'refunds.stage.BankReturned', tone: 'danger' },
  Replaced: { key: 'refunds.stage.Replaced', tone: 'neutral' },
};

export const REFUND_PURPOSE: Record<RefundPurpose, MessageKey> = {
  Bill: 'refunds.purpose.Bill',
  Deposit: 'refunds.purpose.Deposit',
  Advance: 'refunds.purpose.Advance',
};

/** How the counter paid a refund back, for "Paid back by …" (cash has a sentence of its own). */
export function refundModeKey(mode: RefundMode | null): MessageKey {
  switch (mode) {
    case 'Card':
      return 'refunds.mode.Card';
    case 'Upi':
      return 'refunds.mode.Upi';
    case 'NetBanking':
      return 'refunds.mode.NetBanking';
    case 'Cheque':
      return 'refunds.mode.Cheque';
    case 'Wallet':
      return 'refunds.mode.Wallet';
    default:
      return 'refunds.mode.other';
  }
}

/** What the patient should know of a refund at its stage, with its dates filled in. */
export function refundStatusText(refund: PortalRefund, t: Translate, f: Formatters): string {
  switch (refund.stage) {
    case 'Requested':
      return t('refunds.detail.Requested', { date: f.date(refund.requestedAt) });
    case 'Approved':
      return t('refunds.detail.Approved');
    case 'NotApproved':
      return t('refunds.detail.NotApproved');
    case 'PaidOut':
      return refund.mode === 'Cash'
        ? t('refunds.detail.PaidOutCash', { date: f.date(refund.sentAt) })
        : t('refunds.detail.PaidOut', { mode: t(refundModeKey(refund.mode)), date: f.date(refund.sentAt) });
    case 'KeptAsAdvance':
      return t('refunds.detail.KeptAsAdvance', { date: f.date(refund.sentAt) });
    case 'WithTheBank':
      return t('refunds.detail.WithTheBank', { date: f.date(refund.sentAt) });
    case 'Credited':
      return t('refunds.detail.Credited', { date: f.date(refund.creditedAt ?? refund.sentAt) });
    case 'BankReturned':
      return t('refunds.detail.BankReturned');
    case 'Replaced':
      return t('refunds.detail.Replaced');
  }
}

/** The reference to trace a refund by: the bank's for one sent through the gateway, the counter's otherwise. */
export function refundReferenceText(refund: PortalRefund, t: Translate): string | null {
  if (!refund.reference) {
    return null;
  }

  return refund.stage === 'Credited' || refund.stage === 'WithTheBank'
    ? t('refunds.bankReference', { reference: refund.reference })
    : t('refunds.reference', { reference: refund.reference });
}

/** Money is compared in paise so ₹0.004 rounding noise never shows a bill as "due". */
export function isDue(amount: number): boolean {
  return Math.round(amount * 100) > 0;
}

/** Total still to pay across bills (in rupees, rounded to paise). */
export function totalDue(bills: readonly { dueAmount: number }[]): number {
  return bills.reduce((sum, bill) => sum + Math.max(0, Math.round(bill.dueAmount * 100)), 0) / 100;
}
