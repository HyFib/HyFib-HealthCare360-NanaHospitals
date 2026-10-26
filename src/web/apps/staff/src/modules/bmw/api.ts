import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Bio-medical waste (server `WasteEndpoints`): bags bar-coded and weighed where the waste is generated, handed over to
// the common bio-medical waste treatment facility (CBWTF) within 48 hours, the month's register the hospital publishes,
// and the year's totals for the annual report (Form IV) to the State Pollution Control Board.

export const WASTE_CATEGORIES = ['Yellow', 'Red', 'White', 'Blue'] as const;
export type WasteCategory = (typeof WASTE_CATEGORIES)[number];
export type WasteBagStatus = 'Collected' | 'HandedOver' | 'Voided';

/** What goes in each colour (BMW Rules 2016, Schedule I), shown where the bag is weighed. */
export const categoryHint: Record<WasteCategory, string> = {
  Yellow: 'Anatomical and soiled waste, discarded medicines, chemical, lab and cytotoxic waste',
  Red: 'Contaminated recyclable plastics: tubing, bottles, catheters, gloves',
  White: 'Sharps, metals included, in a translucent puncture-proof container',
  Blue: 'Glassware and metallic body implants, in a blue-marked box',
};

export interface WasteBag {
  id: string;
  barcode: string;
  category: WasteCategory;
  departmentId: string | null;
  source: string;
  weightKg: number;
  collectedAt: string;
  collectedByName: string;
  status: WasteBagStatus;
  overdue: boolean;
  handoverId: string | null;
  voidReason: string | null;
  version: number;
}

export interface CategoryTotal {
  category: WasteCategory;
  bags: number;
  kg: number;
}

export interface PendingWaste {
  bags: WasteBag[];
  byCategory: CategoryTotal[];
  overdue: number;
}

export interface WasteHandover {
  id: string;
  number: string;
  handedOverAt: string;
  cbwtf: string;
  vehicleNumber: string;
  receivedBy: string;
  receiptNumber: string | null;
  handedOverByName: string;
  bagCount: number;
  totalKg: number;
  lateBags: number;
  byCategory: CategoryTotal[];
}

export interface WasteMonth {
  year: number;
  month: number;
  days: { date: string; byCategory: CategoryTotal[]; totalKg: number }[];
  totals: CategoryTotal[];
  handovers: number;
  lateBags: number;
}

export interface WasteYear {
  year: number;
  authorisationNumber: string | null;
  cbwtf: string | null;
  totals: CategoryTotal[];
  totalKg: number;
  averageKgPerDay: number;
  handovers: number;
  lateBags: number;
  voidedBags: number;
}

export interface RecordBagRequest {
  category: WasteCategory;
  departmentId: string | null;
  source: string | null;
  weightKg: number;
  collectedAt: string | null;
}

export interface HandOverRequest {
  barcodes: string[];
  cbwtf: string | null;
  vehicleNumber: string;
  receivedBy: string;
  receiptNumber: string | null;
}

export const bmwKeys = {
  all: ['bmw'] as const,
  pending: () => ['bmw', 'pending'] as const,
  handovers: () => ['bmw', 'handovers'] as const,
  month: (year: number, month: number) => ['bmw', 'month', year, month] as const,
  year: (year: number) => ['bmw', 'year', year] as const,
};

const base = '/api/v1/bmw';

export const bmwQueries = {
  pending: () =>
    queryOptions({
      queryKey: bmwKeys.pending(),
      queryFn: ({ signal }) => api.get<PendingWaste>(`${base}/bags/pending`, { signal }),
    }),
  handovers: () =>
    queryOptions({
      queryKey: bmwKeys.handovers(),
      queryFn: ({ signal }) => api.get<WasteHandover[]>(`${base}/handovers`, { signal }),
    }),
  month: (year: number, month: number) =>
    queryOptions({
      queryKey: bmwKeys.month(year, month),
      queryFn: ({ signal }) => api.get<WasteMonth>(`${base}/register`, { query: { year, month }, signal }),
    }),
  year: (year: number) =>
    queryOptions({
      queryKey: bmwKeys.year(year),
      queryFn: ({ signal }) => api.get<WasteYear>(`${base}/annual`, { query: { year }, signal }),
    }),
};

export const bmwApi = {
  record: (request: RecordBagRequest) => api.post<WasteBag>(`${base}/bags`, request),
  void: (id: string, reason: string, version: number) =>
    api.post<WasteBag>(`${base}/bags/${id}/void`, { reason, version }),
  handOver: (request: HandOverRequest) => api.post<WasteHandover>(`${base}/handovers`, request),
  exportMonth: (year: number, month: number) => api.blob(`${base}/register/export`, { query: { year, month } }),
};

export const BMW_PERMISSIONS = {
  /** Weigh and bar-code bags where the waste is generated (nurses, lab, CSSD, OT, quality). */
  record: 'bmw.record',
  /** The handover to the CBWTF, the register and the annual report (quality, administration). */
  manage: 'bmw.manage',
} as const;

export const BMW_READERS = [BMW_PERMISSIONS.record, BMW_PERMISSIONS.manage] as const;

/** What stops a bag from being recorded: a category, where it came from, and a weight above 0 and at most 50 kg. */
export function bagProblems(input: { category: string; source: string; weight: string }) {
  const kg = Number(input.weight);
  return {
    category: input.category ? null : 'Choose the colour of the bag.',
    source: input.source.trim() ? null : 'Say where the waste came from.',
    weight:
      input.weight.trim() && Number.isFinite(kg) && kg > 0 && kg <= 50
        ? null
        : 'Weigh the bag: more than 0 and at most 50 kg.',
  };
}

/** The barcodes typed or scanned for a handover, one per line or separated by commas or spaces, each once. */
export function parseBarcodes(text: string): string[] {
  return [
    ...new Set(
      text
        .split(/[\s,;]+/)
        .map((b) => b.trim().toUpperCase())
        .filter(Boolean),
    ),
  ];
}
