import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * GST e-invoice register: /api/v1/billing/einvoices (GST rule 48(4)). B2B tax invoices (`bill`: bills to payers
 * registered for GST; `pharmacy`: the pharmacy's B2B sales) and the credit notes against them (`creditnote`: a bill's
 * credit notes; `pharmacyreturn`: returns of B2B sales) are downloaded as the portal's JSON, uploaded to the Invoice
 * Registration Portal, and the IRN it gives back is recorded with its signed QR code; a facility connected to the portal
 * (`integrations.irp`) asks for the IRN itself, cancels it there within 24 hours, and generates the e-way bill of a
 * pharmacy invoice of goods above the threshold. A credit note (`CRN`) or debit note (`DBN`, `debitnote`) names the
 * invoice it changes and goes after it. Permission `billing.einvoice`.
 */

export type EInvoiceSource = 'bill' | 'pharmacy' | 'creditnote' | 'debitnote' | 'pharmacyreturn';

/** The portal's document type: a tax invoice, or a credit or debit note against one. */
export type EInvoiceType = 'INV' | 'CRN' | 'DBN';

/** Mirrors `TransportMode`: how the goods of an e-way bill move. */
export type TransportMode = 'Road' | 'Rail' | 'Air' | 'Ship';

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
  type: EInvoiceType;
  /** For a credit note: the number of the invoice it reduces. */
  against: string | null;
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
  /** `manual` (recorded by hand), or the portal provider HC360 asked: `nic`, `gsp` or `simulator`. */
  provider: string;
  ewbNo: number | null;
  ewbDate: string | null;
  ewbValidTill: string | null;
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
  /** What stops the portal from registering the document; empty when its JSON is ready. */
  problems: string[];
  registration: EInvoiceRegistration | null;
  type: EInvoiceType;
  /** For a credit note: the invoice it reduces, and its date. */
  against: string | null;
  againstDate: string | null;
  /** The facility is connected to the portal: HC360 asks for the IRN itself. */
  portalConnected: boolean;
  /** An invoice of goods above the e-way bill threshold (CGST rule 138). */
  ewayBillNeeded: boolean;
}

export interface GenerateEwayBill {
  /** 0: the portal works it out from the PIN codes. */
  distanceKm: number;
  mode: TransportMode;
  vehicleNumber: string | null;
  transporterGstin: string | null;
  transporterName: string | null;
  transportDocumentNumber: string | null;
  transportDocumentDate: string | null;
}

/** The IRN printed on an invoice (`EInvoiceStamp`), on bills and pharmacy sales that have one. */
export interface EInvoiceStamp {
  irn: string;
  ackNo: string;
  ackDate: string;
  signedQrCode: string;
  qrCodeSvg: string;
}

/**
 * Mirrors `MissingIrn`: why a document due an IRN has no live one, as the register says. `Pending` until its IRN is asked
 * of the portal or recorded; `IrnCancelled` once its IRN was cancelled on the portal, which never registers the same
 * number again.
 */
export type MissingIrn = 'Pending' | 'IrnCancelled';

/**
 * What a B2B tax invoice due an IRN prints in place of the IRN while it has none: it is not a valid tax invoice for the
 * buyer (GST rule 48(5)). The same words as the server's A4 bill.
 */
export function missingIrnNotice(missing: MissingIrn): string {
  return missing === 'IrnCancelled'
    ? 'IRN cancelled: this is not a valid tax invoice for the buyer (GST rule 48(5)).'
    : 'E-invoice pending: this is not yet a valid tax invoice for the buyer (GST rule 48(5)).';
}

export const EINVOICE_PERMISSION = 'billing.einvoice';

/** Where each document comes from, as the register names it. */
export const SOURCE_LABELS: Record<EInvoiceSource, string> = {
  bill: 'Bill',
  pharmacy: 'Pharmacy',
  creditnote: 'Credit note',
  debitnote: 'Debit note',
  pharmacyreturn: 'Pharmacy return',
};

/** "NHCN26000001 (Credit note against NHOB260000123)": the document, its kind, and the invoice a credit note reduces. */
export function documentLabel(row: {
  number: string;
  source: EInvoiceSource;
  type: EInvoiceType;
  against: string | null;
}): string {
  return row.type !== 'INV' && row.against
    ? `${row.number} (${SOURCE_LABELS[row.source]} against ${row.against})`
    : `${row.number} (${SOURCE_LABELS[row.source]})`;
}

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
  /** Asks the connected portal for the IRN. */
  generate: (source: EInvoiceSource, id: string) => api.post<EInvoiceDetail>(`${base}/${source}/${id}/irn/generate`),
  ewayBill: (source: EInvoiceSource, id: string, body: GenerateEwayBill) =>
    api.post<EInvoiceDetail>(`${base}/${source}/${id}/ewaybill`, body),
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
