/**
 * Billing enums exactly as the server sends them (strings), with the plain words shown to staff. Pure module: no
 * React, no API client, so calculators, print templates and tests can use it.
 */

export const BILL_TYPES = ['OutPatient', 'InPatient'] as const;
export type BillType = (typeof BILL_TYPES)[number];
export const BILL_TYPE_LABEL: Record<BillType, string> = { OutPatient: 'OP', InPatient: 'IP' };

export const BILL_STATUSES = ['Draft', 'Finalized', 'PartiallyPaid', 'Paid', 'Cancelled', 'Refunded'] as const;
export type BillStatus = (typeof BILL_STATUSES)[number];
export const BILL_STATUS_LABEL: Record<BillStatus, string> = {
  Draft: 'Draft',
  Finalized: 'Unpaid',
  PartiallyPaid: 'Partly paid',
  Paid: 'Paid',
  Cancelled: 'Cancelled',
  Refunded: 'Cancelled, refunded',
};

export const CHARGE_STATUSES = ['Unbilled', 'Billed', 'Cancelled'] as const;
export type ChargeStatus = (typeof CHARGE_STATUSES)[number];

export const SERVICE_CATEGORIES = [
  'Registration',
  'Consultation',
  'Laboratory',
  'Radiology',
  'Cardiology',
  'Procedure',
  'Surgery',
  'BedCharge',
  'Nursing',
  'DoctorVisit',
  'Package',
  'HealthCheckup',
  'Physiotherapy',
  'Dialysis',
  'Dietary',
  'BloodBank',
  'Ambulance',
  'Consumable',
  'Miscellaneous',
] as const;
export type ServiceCategory = (typeof SERVICE_CATEGORIES)[number];
export const SERVICE_CATEGORY_LABEL: Record<ServiceCategory, string> = {
  Registration: 'Registration',
  Consultation: 'Consultation',
  Laboratory: 'Laboratory',
  Radiology: 'Radiology / imaging',
  Cardiology: 'Cardiology',
  Procedure: 'Procedure',
  Surgery: 'Surgery / OT',
  BedCharge: 'Room / bed',
  Nursing: 'Nursing',
  DoctorVisit: 'Doctor visit',
  Package: 'Package',
  HealthCheckup: 'Health check-up',
  Physiotherapy: 'Physiotherapy',
  Dialysis: 'Dialysis',
  Dietary: 'Diet / food',
  BloodBank: 'Blood bank',
  Ambulance: 'Ambulance',
  Consumable: 'Medicines / consumables',
  Miscellaneous: 'Other',
};

export const PAYMENT_MODES = ['Cash', 'Card', 'Upi', 'NetBanking', 'Cheque', 'PaymentGateway', 'Insurance', 'Wallet', 'Advance'] as const;
export type PaymentMode = (typeof PAYMENT_MODES)[number];
export const PAYMENT_MODE_LABEL: Record<PaymentMode, string> = {
  Cash: 'Cash',
  Card: 'Card',
  Upi: 'UPI',
  NetBanking: 'Bank transfer',
  Cheque: 'Cheque',
  PaymentGateway: 'Online payment',
  Insurance: 'Insurance settlement',
  Wallet: 'Wallet',
  Advance: 'Advance adjusted',
};

/** Modes a cashier may key in at the counter (advances, insurance and gateway payments are recorded by the system). */
export const COUNTER_PAYMENT_MODES = ['Cash', 'Card', 'Upi', 'NetBanking', 'Cheque', 'Wallet'] as const satisfies readonly PaymentMode[];
export type CounterPaymentMode = (typeof COUNTER_PAYMENT_MODES)[number];

/** Ways money can be returned to a patient (PayRefundRequest). */
export const REFUND_PAYOUT_MODES = ['Cash', 'Card', 'Upi', 'NetBanking', 'Cheque', 'PaymentGateway'] as const satisfies readonly PaymentMode[];
export const REFUND_PAYOUT_MODE_LABEL: Record<(typeof REFUND_PAYOUT_MODES)[number], string> = {
  Cash: 'Cash from the counter',
  Card: 'Card reversal',
  Upi: 'UPI transfer',
  NetBanking: 'Bank transfer (NEFT/IMPS)',
  Cheque: 'Cheque',
  PaymentGateway: 'Reverse the online payment',
};

/** "Cash + Upi" (receipt mode summary from the server) → "Cash + UPI". */
export function modeSummaryLabel(summary: string | null | undefined): string {
  if (!summary) {
    return '—';
  }
  return summary
    .split('+')
    .map((part) => part.trim())
    .map((mode) => (mode in PAYMENT_MODE_LABEL ? PAYMENT_MODE_LABEL[mode as PaymentMode] : mode))
    .join(' + ');
}

export const RECEIPT_PURPOSES = ['BillPayment', 'Deposit', 'PayerSettlement', 'Advance'] as const;
export type ReceiptPurpose = (typeof RECEIPT_PURPOSES)[number];
export const RECEIPT_PURPOSE_LABEL: Record<ReceiptPurpose, string> = {
  BillPayment: 'Bill payment',
  Deposit: 'IP advance',
  PayerSettlement: 'Insurer settlement',
  Advance: 'Patient advance',
};

export const RECEIPT_SOURCES = ['Counter', 'PosTerminal', 'PaymentGateway', 'InsuranceDesk', 'DepositAdjustment'] as const;
export type ReceiptSource = (typeof RECEIPT_SOURCES)[number];
export const RECEIPT_SOURCE_LABEL: Record<ReceiptSource, string> = {
  Counter: 'Counter',
  PosTerminal: 'Card/UPI terminal',
  PaymentGateway: 'Online',
  InsuranceDesk: 'Insurance desk',
  DepositAdjustment: 'Advance adjustment',
};

export const REFUND_STATUSES = ['Requested', 'Approved', 'Rejected', 'Paid'] as const;
export type RefundStatus = (typeof REFUND_STATUSES)[number];
export const REFUND_STATUS_LABEL: Record<RefundStatus, string> = {
  Requested: 'Awaiting approval',
  Approved: 'Approved, to pay',
  Rejected: 'Rejected',
  Paid: 'Paid',
};

export const REFUND_PURPOSES = ['Bill', 'Deposit', 'Advance'] as const;
export type RefundPurpose = (typeof REFUND_PURPOSES)[number];
export const REFUND_PURPOSE_LABEL: Record<RefundPurpose, string> = {
  Bill: 'Bill refund',
  Deposit: 'Unused IP advance',
  Advance: 'Unused patient advance',
};

export const CREDIT_NOTE_KINDS = ['CreditNote', 'WriteOff'] as const;
export type CreditNoteKind = (typeof CREDIT_NOTE_KINDS)[number];
export const CREDIT_NOTE_KIND_LABEL: Record<CreditNoteKind, string> = { CreditNote: 'Credit note', WriteOff: 'Write-off (bad debt)' };

export const DISCOUNT_APPROVAL_STATUSES = ['Pending', 'Approved', 'Rejected', 'Withdrawn'] as const;
export type DiscountApprovalStatus = (typeof DISCOUNT_APPROVAL_STATUSES)[number];
export const DISCOUNT_APPROVAL_STATUS_LABEL: Record<DiscountApprovalStatus, string> = {
  Pending: 'Awaiting decision',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Withdrawn: 'Withdrawn',
};

export const SHIFT_STATUSES = ['Open', 'Closed'] as const;
export type ShiftStatus = (typeof SHIFT_STATUSES)[number];

export const PRICE_SOURCES = ['BasePrice', 'TariffPlan', 'Override', 'FreeFollowUp'] as const;
export type PriceSource = (typeof PRICE_SOURCES)[number];
export const PRICE_SOURCE_LABEL: Record<PriceSource, string> = {
  BasePrice: 'Standard price',
  TariffPlan: 'Tariff price',
  Override: 'Special price',
  FreeFollowUp: 'Free follow-up',
};

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

export const GATEWAY_ORDER_STATUSES = ['Created', 'Paid', 'Failed', 'Expired', 'Cancelled'] as const;
export type GatewayOrderStatus = (typeof GATEWAY_ORDER_STATUSES)[number];
export const GATEWAY_ORDER_STATUS_LABEL: Record<GatewayOrderStatus, string> = {
  Created: 'Waiting for payment',
  Paid: 'Paid',
  Failed: 'Failed',
  Expired: 'Expired',
  Cancelled: 'Cancelled',
};
export type GatewayOrderKind = 'CheckoutOrder' | 'PaymentLink';
export type PaymentChannel = 'Staff' | 'Portal';

export const IP_ACCOUNT_STATUSES = ['Open', 'DischargeInitiated', 'Discharged', 'Closed', 'Cancelled'] as const;
export type IpAccountStatus = (typeof IP_ACCOUNT_STATUSES)[number];
export const IP_ACCOUNT_STATUS_LABEL: Record<IpAccountStatus, string> = {
  Open: 'In hospital',
  DischargeInitiated: 'Discharge in progress',
  Discharged: 'Discharged, dues open',
  Closed: 'Settled',
  Cancelled: 'Admission cancelled',
};

export const TARIFF_TYPES = ['General', 'Corporate', 'Insurance', 'GovernmentScheme', 'Staff'] as const;
export type TariffType = (typeof TARIFF_TYPES)[number];
export const TARIFF_TYPE_LABEL: Record<TariffType, string> = {
  General: 'General (walk-in)',
  Corporate: 'Corporate',
  Insurance: 'Insurance',
  GovernmentScheme: 'Government scheme',
  Staff: 'Staff',
};

export const TALLY_EXPORT_STATUSES = ['Queued', 'Posted', 'NotConfigured', 'Failed'] as const;
export type TallyExportStatus = (typeof TALLY_EXPORT_STATUSES)[number];
export const TALLY_EXPORT_STATUS_LABEL: Record<TallyExportStatus, string> = {
  Queued: 'Waiting to post',
  Posted: 'Posted to Tally',
  NotConfigured: 'Ready to download',
  Failed: 'Failed',
};

export const PAYER_TYPES = ['Self', 'Insurance', 'Corporate', 'GovernmentScheme', 'Staff', 'Charity'] as const;
export type PayerTypeValue = (typeof PAYER_TYPES)[number];
export const PAYER_TYPE_LABEL: Record<PayerTypeValue, string> = {
  Self: 'Self pay',
  Insurance: 'Insurance',
  Corporate: 'Corporate',
  GovernmentScheme: 'Government scheme',
  Staff: 'Staff',
  Charity: 'Charity',
};

/** Payer types whose bills are split with a third party (BillPayerTerms.HasPayer). */
export function isThirdPartyPayer(payerType: string, payerId: string | null | undefined): boolean {
  return Boolean(payerId) && (payerType === 'Insurance' || payerType === 'Corporate' || payerType === 'GovernmentScheme');
}

export function labelOf<T extends string>(labels: Record<T, string>, value: T | null | undefined): string {
  return value ? (labels[value] ?? value) : '—';
}

export function optionsOf<T extends string>(values: readonly T[], labels: Record<T, string>): { value: T; label: string }[] {
  return values.map((value) => ({ value, label: labels[value] }));
}
