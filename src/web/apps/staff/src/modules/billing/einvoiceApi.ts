import { isApiError } from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * GST e-invoice register: /api/v1/billing/einvoices (GST rule 48(4)). B2B tax invoices (`bill`: bills to payers
 * registered for GST; `pharmacy`: the pharmacy's B2B sales) and the credit notes against them (`creditnote`: a bill's
 * credit notes; `pharmacyreturn`: returns of B2B sales) are downloaded as the portal's JSON, uploaded to the Invoice
 * Registration Portal, and the IRN it gives back is recorded with its signed QR code; a facility connected to the portal
 * (`integrations.irp`) asks for the IRN itself, cancels it there within 24 hours, and generates the e-way bill of a
 * pharmacy invoice of goods above the threshold; an e-way bill generated elsewhere (on the e-way bill portal, by the
 * transporter) is recorded from its print. A credit note (`CRN`) or debit note (`DBN`, `debitnote`) names the invoice it
 * changes and goes after it. Permission `billing.einvoice`.
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

/** An e-way bill generated outside HC360, as its print (EWB-01) shows it. */
export interface RecordEwayBill {
  /** 12 digits, or 14 for one generated from 1 January 2027; spaces between groups of digits are allowed. */
  ewbNo: string;
  generatedAt: string;
  /** Null while only Part-A is filled: the e-way bill has no validity until its Part-B is. */
  validUntil: string | null;
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
  /** Records an e-way bill generated outside HC360, from its print; the server checks it with the portal when connected. */
  recordEwayBill: (source: EInvoiceSource, id: string, body: RecordEwayBill) =>
    api.post<EInvoiceDetail>(`${base}/${source}/${id}/ewaybill/record`, body),
};

/**
 * The calls that change a document's IRN: recording one by hand, asking the portal for one, cancelling it, and, past the
 * 24 hours, asking the portal whether a cancellation made in time went through (the same cancel request, read back);
 * and recording an e-way bill generated elsewhere on its live IRN.
 */
export type IrnAction = 'record' | 'generate' | 'cancel' | 'askCancelled' | 'recordEwayBill';

/**
 * A refusal of one of those calls as the dialog shows it: `warning` when nothing is wrong with the document and trying
 * again settles it, `error` when something has to change first; a heading in plain words; and the server's own sentence,
 * which says what was found and what to do (empty when the error did not come from the server).
 */
export interface IrnAnswer {
  intent: 'warning' | 'error';
  title: string;
  detail: string;
}

type Heading = readonly [intent: IrnAnswer['intent'], title: string];

// A heading of its own only where the outcome is not the call's plain "not done" (UNEXPLAINED): still open, as when the
// HC360 server or the portal did not answer, or done already. Any other refusal gets the call's heading over the
// server's sentence, which names what was found and what to do.

/** Whichever call it answers. */
const COMMON_HEADINGS: Record<string, Heading> = {
  network: ['warning', 'Cannot reach the HealthCare360 server'],
  concurrency: ['warning', 'Changed elsewhere: the latest is shown, try again'],
  'einvoice.registered': ['warning', 'Already registered'],
};

/** By call: the portal not answering means something different to each (billing.md, "Checks and lost answers"). */
const HEADINGS: Record<IrnAction, Record<string, Heading>> = {
  record: {
    'einvoice.irp_unconfirmed': ['error', 'Not recorded: compare what was entered with the portal'],
    'einvoice.irp_unreachable': ['warning', 'Not recorded yet: the portal could not be asked'],
  },
  generate: {
    'einvoice.irp_unreachable': ['warning', 'No IRN yet: the portal did not answer'],
    'einvoice.irp_cancelled': ['error', 'Cancelled on the portal: it needs a new number'],
  },
  // Not known: the server's sentence says what to do, try again (the portal did not answer) or look the IRN up on the
  // portal (it would not say how it holds the IRN, as after the days it keeps an IRN's details).
  cancel: {
    'einvoice.irp_cancel_unknown': ['warning', 'Not known whether the portal cancelled the IRN'],
    'einvoice.irp_unreachable': ['warning', 'Not cancelled: the IRN is still active on the portal'],
  },
  askCancelled: {
    'einvoice.irp_cancel_unknown': ['warning', 'Not known whether the portal cancelled the IRN'],
    'einvoice.cancel_window': ['error', 'Not cancelled on the portal: the IRN stays'],
  },
  // The portal confirms only an e-way bill it generated from the IRN; one it holds none for is recorded on its print.
  recordEwayBill: {
    'einvoice.ewaybill_unconfirmed': ['error', 'Not recorded: the portal does not confirm this e-way bill'],
    'einvoice.irp_ewaybill_cancelled': ['error', 'Cancelled on the portal: the goods need a new e-way bill'],
    'einvoice.irp_unreachable': ['warning', 'Not recorded yet: the portal could not be asked'],
    'einvoice.ewaybill_number_exists': ['error', 'Already recorded against an invoice'],
  },
};

const UNEXPLAINED: Record<IrnAction, string> = {
  record: 'The IRN was not recorded',
  generate: 'The portal gave no IRN',
  cancel: 'The cancellation was not recorded',
  askCancelled: 'No cancellation was recorded',
  recordEwayBill: 'The e-way bill was not recorded',
};

/** The problem codes the dialog has words for, by call (each call's own and the common ones). */
export function irnAnswerCodes(action: IrnAction): string[] {
  return [...Object.keys(HEADINGS[action]), ...Object.keys(COMMON_HEADINGS)];
}

/** What the server answered to `action`, in plain words: see {@link IrnAnswer}. */
export function irnAnswer(action: IrnAction, error: unknown): IrnAnswer {
  if (!isApiError(error)) {
    return { intent: 'error', title: UNEXPLAINED[action], detail: '' };
  }
  const code = error.code ?? '';
  const [intent, title] = HEADINGS[action][code] ?? COMMON_HEADINGS[code] ?? ['error', UNEXPLAINED[action]];
  return { intent, title, detail: error.userMessage };
}

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

/** The first day (the hospital's) of 14-digit e-way bill numbers: GSTN advisory 687 of 4 November 2026. */
export const FOURTEEN_DIGITS_FROM = '2027-01-01';

/** A `datetime-local` value (the browser's local time) as the instant the server takes; null when incomplete. */
export function localToIso(local: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(local)) {
    return null;
  }
  const at = new Date(local);
  return Number.isNaN(at.getTime()) ? null : at.toISOString();
}

const DAY_MS = 86_400_000;

/** Whole days from one `yyyy-mm-dd` date to another. */
function daysBetween(from: string, to: string): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / DAY_MS);
}

/**
 * What stops an e-way bill entered from its print from being recorded, as the server checks it (`EwayBillEntries`): its
 * number is 12 digits, or 14 from 1 January 2027; it was generated on or after the invoice's date, within 180 days of it,
 * and not later than now; it is valid until a time after that and within 360 days, or blank while only Part-A is filled.
 * The times are the browser's local ones, as `datetime-local` gives them: the hospital's, as the print shows them.
 */
export function ewayBillProblems(
  ewbNo: string,
  generatedAt: string,
  validUntil: string,
  invoiceDate: string,
  now: Date = new Date(),
): string[] {
  const problems: string[] = [];
  const digits = ewbNo.replace(/ /g, '');
  const generated = localToIso(generatedAt);
  const day = generatedAt.slice(0, 10);
  if (!/^[1-9](\d{11}|\d{13})$/.test(digits)) {
    problems.push(
      'Enter the e-way bill number: the 12 digits on its print (14 for one generated from 1 January 2027).',
    );
  } else if (digits.length === 14 && generated && day < FOURTEEN_DIGITS_FROM) {
    problems.push('E-way bill numbers have 14 digits only from 1 January 2027.');
  }
  if (!generated) {
    problems.push('Enter when the e-way bill was generated, as its print shows.');
  } else {
    if (Date.parse(generated) > now.getTime() + 10 * 60_000) {
      problems.push('The e-way bill is dated in the future.');
    }
    if (day < invoiceDate) {
      problems.push('The e-way bill is dated before the invoice it names.');
    } else if (day >= '2025-01-01' && daysBetween(invoiceDate, day) > 180) {
      problems.push('The portal generates an e-way bill only for an invoice at most 180 days old.');
    }
  }
  if (validUntil) {
    const until = localToIso(validUntil);
    if (!until) {
      problems.push('Enter the end of its validity in full, or leave it blank while only Part-A is filled.');
    } else if (generated && Date.parse(until) <= Date.parse(generated)) {
      problems.push('The e-way bill must be valid until a time after it was generated.');
    } else if (generated && Date.parse(until) > Date.parse(generated) + 360 * DAY_MS) {
      problems.push('The portal extends an e-way bill to at most 360 days after its generation.');
    }
  }
  return problems;
}
