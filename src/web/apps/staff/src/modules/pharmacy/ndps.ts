import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { StockMovementType } from '@/modules/inventory/labels';
import { PHARMACY_PERMISSIONS } from './permissions';

// The NDPS accounts of the essential narcotic drugs (server `NdpsEndpoints`), read from the controlled-drug register:
// a store's day (Form 3H), a patient's record (Form 3E), the year's return with next year's estimate (Form 3J), and the
// blind two-person count at a change of shift.

export interface NdpsMovement {
  at: string;
  type: StockMovementType;
  in: number;
  out: number;
  balanceAfter: number;
  reference: string | null;
  patientUhid: string | null;
  patientName: string | null;
  prescriberName: string | null;
  prescriptionNumber: string | null;
}

export interface NdpsDailyDrug {
  itemId: string;
  itemName: string;
  opening: number;
  received: number;
  dispensed: number;
  otherIn: number;
  otherOut: number;
  closing: number;
  balanced: boolean;
  movements: NdpsMovement[];
}

export interface NdpsDailyAccount {
  date: string;
  storeId: string;
  storeName: string;
  drugs: NdpsDailyDrug[];
}

export interface NdpsAnnualDrug {
  itemId: string;
  itemName: string;
  opening: number;
  received: number;
  consumed: number;
  writtenOff: number;
  closing: number;
  balanced: boolean;
  estimateNextYear: number;
}

export interface NdpsAnnualReturn {
  year: number;
  drugs: NdpsAnnualDrug[];
}

export interface NdpsPatientLine {
  at: string;
  itemName: string;
  batchNumber: string;
  quantity: number;
  type: StockMovementType;
  prescriberName: string | null;
  prescriberRegistrationNumber: string | null;
  prescriptionNumber: string | null;
  reference: string | null;
}

export interface NdpsPatientRecord {
  patientId: string;
  patientName: string;
  uhid: string;
  sex: string | null;
  ageYears: number | null;
  address: string | null;
  lines: NdpsPatientLine[];
}

export interface NdpsStore {
  id: string;
  name: string;
}

export type NarcoticCountStatus = 'Counted' | 'Witnessed';

export interface NarcoticCountLine {
  itemId: string;
  itemName: string;
  registerBalance: number;
  counted: number;
  difference: number;
}

export interface NarcoticCount {
  id: string;
  storeId: string;
  storeName: string;
  countedAt: string;
  countedByName: string;
  status: NarcoticCountStatus;
  witnessedAt: string | null;
  witnessedByName: string | null;
  explanation: string | null;
  hasDifference: boolean;
  lines: NarcoticCountLine[];
  version: number;
}

export interface NarcoticCountSheet {
  storeId: string;
  storeName: string;
  items: { itemId: string; itemName: string }[];
}

export const ndpsKeys = {
  all: ['pharmacy', 'ndps'] as const,
  stores: () => ['pharmacy', 'ndps', 'stores'] as const,
  daily: (storeId: string, date: string) => ['pharmacy', 'ndps', 'daily', storeId, date] as const,
  annual: (year: number) => ['pharmacy', 'ndps', 'annual', year] as const,
  patient: (id: string) => ['pharmacy', 'ndps', 'patient', id] as const,
  counts: () => ['pharmacy', 'ndps', 'counts'] as const,
  sheet: (storeId: string) => ['pharmacy', 'ndps', 'sheet', storeId] as const,
};

const base = '/api/v1/pharmacy/ndps';

export const ndpsQueries = {
  stores: () =>
    queryOptions({
      queryKey: ndpsKeys.stores(),
      queryFn: ({ signal }) => api.get<NdpsStore[]>(`${base}/stores`, { signal }),
    }),
  daily: (storeId: string, date: string) =>
    queryOptions({
      queryKey: ndpsKeys.daily(storeId, date),
      queryFn: ({ signal }) => api.get<NdpsDailyAccount>(`${base}/daily`, { query: { storeId, date }, signal }),
    }),
  annual: (year: number) =>
    queryOptions({
      queryKey: ndpsKeys.annual(year),
      queryFn: ({ signal }) => api.get<NdpsAnnualReturn>(`${base}/annual`, { query: { year }, signal }),
    }),
  patient: (id: string) =>
    queryOptions({
      queryKey: ndpsKeys.patient(id),
      queryFn: ({ signal }) => api.get<NdpsPatientRecord>(`${base}/patients/${id}`, { signal }),
    }),
  counts: () =>
    queryOptions({
      queryKey: ndpsKeys.counts(),
      queryFn: ({ signal }) => api.get<NarcoticCount[]>(`${base}/counts`, { signal }),
    }),
  sheet: (storeId: string) =>
    queryOptions({
      queryKey: ndpsKeys.sheet(storeId),
      queryFn: ({ signal }) => api.get<NarcoticCountSheet>(`${base}/counts/sheet`, { query: { storeId }, signal }),
    }),
};

export const ndpsApi = {
  exportDaily: (storeId: string, date: string) => api.blob(`${base}/daily/export`, { query: { storeId, date } }),
  exportAnnual: (year: number) => api.blob(`${base}/annual/export`, { query: { year } }),
  count: (storeId: string, lines: { itemId: string; counted: number }[], explanation: string | null) =>
    api.post<NarcoticCount>(`${base}/counts`, { storeId, lines, explanation }),
  witness: (id: string, explanation: string | null, version: number) =>
    api.post<NarcoticCount>(`${base}/counts/${id}/witness`, { explanation, version }),
};

/** Count the narcotics at a change of shift and witness another's count (pharmacists, nurses). */
export const NDPS_COUNT_PERMISSION = PHARMACY_PERMISSIONS.ndpsCount;

/**
 * What stops a count from being recorded: every drug on the sheet counted, as a whole number of units not below zero
 * (quantities as typed). Null when nothing does.
 */
export function countProblem(items: { itemId: string }[], counted: Record<string, string>): string | null {
  const missing = items.filter((i) => (counted[i.itemId] ?? '').trim() === '');
  if (missing.length > 0) {
    return 'Count every drug on the sheet.';
  }
  const bad = items.some((i) => {
    const value = Number(counted[i.itemId]);
    return !Number.isFinite(value) || value < 0;
  });
  return bad ? 'Enter each count as a number not below zero.' : null;
}

/** "+2", "-1", "0": a count's difference from the register. */
export const signed = (n: number) => (n > 0 ? `+${n}` : String(n));
