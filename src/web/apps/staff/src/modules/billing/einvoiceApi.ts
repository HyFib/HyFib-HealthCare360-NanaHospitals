import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * GST e-invoice register: /api/v1/billing/einvoices (GST rule 48(4)). B2B tax invoices (`bill`: bills to payers
 * registered for GST; `pharmacy`: the pharmacy's B2B sales) are downloaded as the portal's JSON, uploaded to the
 * Invoice Registration Portal, and the IRN it gives back is recorded with its signed QR code. Permission
 * `billing.einvoice`.
 */

export type EInvoiceSource = 'bill' | 'pharmacy';

/** Mirrors `EInvoiceState`. */
export type EInvoiceState = 'Pending' | 'Registered' | 'IrnCancelled' | 'NotNeeded';

/** Mirrors `EInvoiceCancelReason`: the portal's own reasons. */
export type EInvoiceCancelReason = 'Duplicate' | 'DataEntryMistake' | 'OrderCancelled' | 'Other';

export interface EInvoiceRow {
  source: EInvoiceSource;
  id: string;
  number: string;
  date: string;
  buyerGstin: string;
  buyerName: string;
  total: number;
  state: EInvoiceState;
  /** Thirty days after the invoice date: the latest the portal takes it from a large taxpayer. */
  reportBy: string;
  isOverdue: boolean;
  irn: string | null;
  ackNo: string | null;
  ackDate: string | null;
}

export interface EInvoiceRegister {
  /** E-invoicing applies to the hospital (setting `gst.einvoice.from`); when not, there are no rows. */
  applicable: boolean;
  applicableFrom: string | null;
  from: string;
  to: string;
  rows: EInvoiceRow[];
}

export interface EInvoiceRegistration {
  irn: string;
  ackNo: string;
  ackDate: string;
  status: 'Registered' | 'Cancelled';
  recordedByName: string;
  recordedAt: string;
  cancelledAt: string | null;
  cancelReason: EInvoiceCancelReason | null;
  cancelRemark: string | null;
  /** The portal cancels an IRN only until 24 hours after it was generated. */
  cancelBy: string;
  canCancel: boolean;
  /** The signed QR code as an SVG data URI. */
  qrCodeSvg: string;
  version: number;
}

export interface EInvoiceDetail {
  source: EInvoiceSource;
  id: string;
  number: string;
  date: string;
  buyerGstin: string;
  buyerName: string;
  placeOfSupply: string;
  lines: number;
  total: number;
  state: EInvoiceState;
  /** What stops the portal from registering the invoice; empty when its JSON is ready. */
  problems: string[];
  registration: EInvoiceRegistration | null;
}

/** The IRN printed on an invoice (`EInvoiceStamp`), on bills and pharmacy sales that have one. */
export interface EInvoiceStamp {
  irn: string;
  ackNo: string;
  ackDate: string;
  signedQrCode: string;
  qrCodeSvg: string;
}

export const EINVOICE_PERMISSION = 'billing.einvoice';

export const CANCEL_REASONS: { value: EInvoiceCancelReason; label: string }[] = [
  { value: 'Duplicate', label: 'Duplicate' },
  { value: 'DataEntryMistake', label: 'Data entry mistake' },
  { value: 'OrderCancelled', label: 'Order cancelled' },
  { value: 'Other', label: 'Other' },
];

export const STATE_LABELS: Record<EInvoiceState, string> = {
  Pending: 'Needs an IRN',
  Registered: 'IRN recorded',
  IrnCancelled: 'IRN cancelled',
  NotNeeded: 'Not needed',
};

export const einvoiceKeys = {
  all: ['einvoices'] as const,
  register: (from: string | undefined, to: string | undefined) =>
    ['einvoices', 'register', from ?? '', to ?? ''] as const,
  detail: (source: EInvoiceSource, id: string) => ['einvoices', source, id] as const,
};

const base = '/api/v1/billing/einvoices';

export const einvoiceQueries = {
  register: (from?: string, to?: string) =>
    queryOptions({
      queryKey: einvoiceKeys.register(from, to),
      queryFn: ({ signal }) => api.get<EInvoiceRegister>(base, { query: { from, to }, signal }),
    }),
  detail: (source: EInvoiceSource, id: string) =>
    queryOptions({
      queryKey: einvoiceKeys.detail(source, id),
      queryFn: ({ signal }) => api.get<EInvoiceDetail>(`${base}/${source}/${id}`, { signal }),
    }),
};

export const einvoiceApi = {
  json: (source: EInvoiceSource, id: string) => api.blob(`${base}/${source}/${id}/json`),
  record: (source: EInvoiceSource, id: string, ackNo: string, signedQrCode: string) =>
    api.post<EInvoiceDetail>(`${base}/${source}/${id}/irn`, { ackNo, signedQrCode }),
  cancel: (source: EInvoiceSource, id: string, reason: EInvoiceCancelReason, remark: string, version: number) =>
    api.post<EInvoiceDetail>(`${base}/${source}/${id}/irn/cancel`, { reason, remark, version }),
};

/** What stops the IRN from being recorded: the portal's 10–20-digit acknowledgement number and its signed QR code (a JWT). */
export function irnProblems(ackNo: string, signedQrCode: string): string[] {
  const problems: string[] = [];
  if (!/^\d{10,20}$/.test(ackNo.trim())) {
    problems.push('Enter the acknowledgement number: the 10 to 20 digits the portal gave.');
  }
  if (signedQrCode.trim().split('.').length !== 3) {
    problems.push('Paste the signed QR code exactly as the portal gave it (three parts separated by dots).');
  }
  return problems;
}
