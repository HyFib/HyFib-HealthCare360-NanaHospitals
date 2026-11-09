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
  /** The weight on the scale at the handover, and whether it differs from where it was filled beyond the tolerance. */
  handoverWeightKg?: number | null;
  weightMismatch?: boolean;
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
  mismatchedBags?: number;
  bags?: WasteBag[];
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

/** One bag scanned at the handover and weighed on the scale before the CBWTF's driver. */
export interface HandoverBag {
  barcode: string;
  weightKg: number;
}

export interface HandOverRequest {
  bags: HandoverBag[];
  cbwtf: string | null;
  vehicleNumber: string;
  receivedBy: string;
  receiptNumber: string | null;
}

/** What a bag's label carries (Schedule IV), from the server: refused until the hospital's HCF identifier is set. */
export interface WasteBagLabelDto {
  barcode: string;
  category: WasteCategory;
  colour: string;
  symbol: string;
  source: string;
  weightKg: number;
  collectedOn: string;
  hcfId: string;
  hcfName: string;
  authorisationNumber: string | null;
}

export const ACCIDENT_KINDS = [
  'NeedleStick',
  'SharpsInjury',
  'Spill',
  'Exposure',
  'Fire',
  'SegregationFailure',
  'TransportAccident',
  'Other',
] as const;
export type WasteAccidentKind = (typeof ACCIDENT_KINDS)[number];

export const ACCIDENT_LABEL: Record<WasteAccidentKind, string> = {
  NeedleStick: 'Needle-stick injury',
  SharpsInjury: 'Injury from other sharps',
  Spill: 'Spill of bio-medical waste',
  Exposure: 'Exposure to infectious or hazardous waste',
  Fire: 'Fire',
  SegregationFailure: 'Waste mixed (segregation failure)',
  TransportAccident: 'Accident while carrying waste',
  Other: 'Other',
};

export interface RecordAccidentRequest {
  occurredAt: string;
  kind: WasteAccidentKind;
  place: string;
  sequence: string;
  category: WasteCategory | null;
  wasteInvolved: string;
  personsAffected: number;
  effects: string;
  emergencyMeasures: string;
  stepsToAlleviate: string | null;
  stepsToPrevent: string | null;
  authorityInformedAt: string | null;
  designation: string | null;
}

export interface WasteAccident extends Omit<RecordAccidentRequest, 'designation'> {
  id: string;
  reportedAt: string;
  reportedByName: string;
  reportedByDesignation: string | null;
}

/** One numbered item of a statutory form, as the form words it, and the answer. */
export interface FormLine {
  number: string;
  item: string;
  answer: string;
}

export interface FormI {
  hcfName: string;
  address: string | null;
  authorisationNumber: string | null;
  hcfId: string | null;
  accident: WasteAccident;
  lines: FormLine[];
}

export interface FormIv {
  year: number;
  hcfName: string;
  address: string | null;
  authorisationNumber: string | null;
  hcfId: string | null;
  cbwtf: string | null;
  beds: number;
  lines: FormLine[];
  generated: CategoryTotal[];
  handedOver: CategoryTotal[];
}

export const bmwKeys = {
  all: ['bmw'] as const,
  accidents: (year: number) => ['bmw', 'accidents', year] as const,
  pending: () => ['bmw', 'pending'] as const,
  handovers: () => ['bmw', 'handovers'] as const,
  month: (year: number, month: number) => ['bmw', 'month', year, month] as const,
  year: (year: number) => ['bmw', 'year', year] as const,
};

const base = '/api/v1/bmw';

export const bmwQueries = {
  accidents: (year: number) =>
    queryOptions({
      queryKey: bmwKeys.accidents(year),
      queryFn: ({ signal }) => api.get<WasteAccident[]>(`${base}/accidents`, { query: { year }, signal }),
    }),
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
  label: (id: string) => api.get<WasteBagLabelDto>(`${base}/bags/${id}/label`),
  recordAccident: (request: RecordAccidentRequest) => api.post<WasteAccident>(`${base}/accidents`, request),
  formI: (id: string) => api.get<FormI>(`${base}/accidents/${id}/form-i`),
  formIv: (year: number) => api.get<FormIv>(`${base}/annual/form-iv`, { query: { year } }),
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

/** How far the weight at handover may differ from where the bag was filled (WasteBag.Tolerance): 5%, at least 0.1 kg. */
export function weightTolerance(filledKg: number): number {
  return Math.max(0.1, Math.round(filledKg * 0.05 * 1000) / 1000);
}

/** Whether a bag weighs differently at the handover from where it was filled: opened, added to or swapped. */
export function weightMismatch(filledKg: number, atHandoverKg: number): boolean {
  return Math.abs(atHandoverKg - filledKg) > weightTolerance(filledKg);
}

/** What stops a handover: every bag scanned weighed on the scale, more than 0 and at most 50 kg. */
export function handoverWeightProblem(
  barcodes: readonly string[],
  weights: Readonly<Record<string, string>>,
): string | null {
  const unweighed = barcodes.filter((b) => {
    const kg = Number(weights[b] ?? '');
    return !(weights[b]?.trim() && Number.isFinite(kg) && kg > 0 && kg <= 50);
  });
  return unweighed.length === 0 ? null : `Weigh every bag at the handover: ${unweighed.join(', ')}.`;
}
