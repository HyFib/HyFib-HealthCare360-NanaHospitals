import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * GST returns of the signed-in facility's GSTIN: /api/v1/billing/gst-returns (`?month=2026-10`). GSTR-1 table by table
 * with the GSTN offline tool's JSON, and GSTR-3B (3.1, 3.2 and table 4). Permission `billing.gst.returns`.
 */

export const GST_RETURNS_PERMISSION = 'billing.gst.returns';

export interface GstAmounts {
  taxable: number;
  igst: number;
  cgst: number;
  sgst: number;
  cess: number;
}

export interface Gstr1Item {
  rate: number;
  amounts: GstAmounts;
}

export interface Gstr1Document {
  number: string;
  date: string;
  buyerGstin: string | null;
  buyerName: string;
  placeOfSupply: string;
  value: number;
  items: Gstr1Item[];
  kind: 'Invoice' | 'CreditNote' | 'DebitNote';
}

export interface Gstr1B2csRow {
  interState: boolean;
  placeOfSupply: string;
  rate: number;
  amounts: GstAmounts;
}

export interface Gstr1NilRow {
  supplyType: string;
  nilRated: number;
  exempt: number;
  nonGst: number;
}

export interface Gstr1HsnRow {
  hsn: string;
  description: string;
  uqc: string;
  quantity: number;
  rate: number;
  amounts: GstAmounts;
}

export interface Gstr1DocumentsIssued {
  nature: number;
  series: string;
  from: string;
  to: string;
  total: number;
  cancelled: number;
  net: number;
}

export interface Gstr1Return {
  gstin: string;
  year: number;
  month: number;
  b2b: Gstr1Document[];
  b2cl: Gstr1Document[];
  b2cs: Gstr1B2csRow[];
  cdnr: Gstr1Document[];
  cdnur: Gstr1Document[];
  nil: Gstr1NilRow[];
  hsnB2b: Gstr1HsnRow[];
  hsnB2c: Gstr1HsnRow[];
  documentsIssued: Gstr1DocumentsIssued[];
  problems: string[];
  period: string;
  /** That the month's GSTR-1 was filed on the portal, as recorded here; null while not recorded. */
  filing?: Gstr1Filing | null;
  /** Paper bills entered this month for earlier months already filed: report them as amendments in this return. */
  lateEntries?: Gstr1LateEntry[] | null;
}

export interface Gstr1Filing {
  year: number;
  month: number;
  arn: string;
  filedOn: string;
  recordedAt: string;
  recordedByName: string | null;
  correctedAt: string | null;
  correctedByName: string | null;
  correctionReason: string | null;
  version: number;
}

export interface Gstr1LateEntry {
  number: string;
  date: string;
  value: number;
  filedYear: number;
  filedMonth: number;
  filedArn: string;
  enteredOn: string;
}

export interface Gstr3bReturn {
  gstin: string;
  year: number;
  month: number;
  outwardTaxable: GstAmounts;
  zeroRated: GstAmounts;
  nilAndExempt: number;
  inwardReverseCharge: GstAmounts;
  nonGst: number;
  interStateToUnregistered: Gstr1B2csRow[];
  itcAllOther: GstAmounts;
  itcReversed: GstAmounts;
  itcNet: GstAmounts;
  purchaseInvoices: number;
  purchasesWithoutGstin: number;
  /** Exempt and nil-rated supplies as a share of turnover (0 to 1), for the CGST rule 42 reversal. */
  exemptShareOfTurnover: number;
  notes: string[];
}

const base = '/api/v1/billing/gst-returns';

export const gstReturnQueries = {
  gstr1: (month: string) =>
    queryOptions({
      queryKey: ['gst-returns', 'gstr1', month] as const,
      queryFn: ({ signal }) => api.get<Gstr1Return>(`${base}/gstr1`, { query: { month }, signal }),
    }),
  gstr3b: (month: string) =>
    queryOptions({
      queryKey: ['gst-returns', 'gstr3b', month] as const,
      queryFn: ({ signal }) => api.get<Gstr3bReturn>(`${base}/gstr3b`, { query: { month }, signal }),
    }),
};

export const gstReturnApi = {
  gstr1Json: (month: string) => api.blob(`${base}/gstr1/json`, { query: { month } }),
  /** Records that the month's GSTR-1 was filed: once per GSTIN and month. */
  markFiled: (month: string, arn: string, filedOn: string) =>
    api.post<Gstr1Filing>(`${base}/gstr1/filing`, { month, arn, filedOn }),
  /** Corrects a recorded filing (a mistyped ARN or date), with the reason. */
  correctFiling: (month: string, arn: string, filedOn: string, reason: string, version: number) =>
    api.put<Gstr1Filing>(`${base}/gstr1/filing`, { month, arn, filedOn, reason, version }),
};

/** The sum of a document's or row's tax: IGST, CGST, SGST and cess. */
export function taxOf(a: GstAmounts): number {
  return Math.round((a.igst + a.cgst + a.sgst + a.cess) * 100) / 100;
}

/** "2026-10": the month before `today`, which is the return usually being prepared. */
export function lastMonth(today: string): string {
  const [year, month] = today.split('-').map(Number) as [number, number];
  return month === 1 ? `${year - 1}-12` : `${year}-${String(month - 1).padStart(2, '0')}`;
}
