import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Linen and laundry (server `LinenEndpoints`): the linen list, the count at each place (the store, each ward against its
// par level, the laundry), and every movement counted in pieces. Wards send soiled linen and report losses; the linen
// store issues, receives and condemns; the laundry washes. Infected linen is kept apart and washed only by thermal
// disinfection (71 °C for 3 minutes, or 65 °C for 10).

export type LinenMovementKind = 'Received' | 'Issued' | 'SoiledReturn' | 'Washed' | 'Condemned' | 'Lost';
export type WashProcess = 'Normal' | 'ThermalDisinfection';
export type CondemnReason = 'Torn' | 'Stained' | 'WornOut';
export type LinenLocation = 'Store' | 'Ward' | 'Laundry';

export const KIND_LABELS: Record<LinenMovementKind, string> = {
  Received: 'New linen received',
  Issued: 'Issued to a ward',
  SoiledReturn: 'Soiled linen from a ward',
  Washed: 'Wash batch',
  Condemned: 'Condemned',
  Lost: 'Lost at a ward',
};

export const CONDEMN_LABELS: Record<CondemnReason, string> = {
  Torn: 'Torn',
  Stained: 'Stained beyond washing',
  WornOut: 'Worn out',
};

export interface LinenItem {
  id: string;
  code: string;
  name: string;
  lifeWashes: number;
  averageWashes: number | null;
  isActive: boolean;
  version: number;
}

export interface LinenItemStock {
  linenItemId: string;
  code: string;
  name: string;
  store: number;
  wards: number;
  soiled: number;
  infected: number;
  inCirculation: number;
}

export interface WardLinen {
  wardId: string;
  wardName: string;
  lines: { linenItemId: string; itemName: string; held: number; par: number; short: number }[];
}

export interface LinenStock {
  items: LinenItemStock[];
  wards: WardLinen[];
}

export interface LinenLine {
  linenItemId: string;
  quantity: number;
}

export interface RecordLinenRequest {
  kind: LinenMovementKind;
  wardId: string | null;
  infected: boolean;
  process: WashProcess | null;
  temperatureC: number | null;
  holdMinutes: number | null;
  condemnReason: CondemnReason | null;
  from: LinenLocation | null;
  notes: string | null;
  lines: LinenLine[];
}

export interface LinenMovement {
  id: string;
  number: string;
  kind: LinenMovementKind;
  wardName: string | null;
  infected: boolean;
  process: WashProcess | null;
  temperatureC: number | null;
  holdMinutes: number | null;
  condemnReason: CondemnReason | null;
  notes: string | null;
  at: string;
  byName: string;
  lines: { linenItemId: string; itemName: string; quantity: number }[];
  pieces: number;
}

export const linenKeys = {
  all: ['linen'] as const,
  items: () => ['linen', 'items'] as const,
  wards: () => ['linen', 'wards'] as const,
  stock: () => ['linen', 'stock'] as const,
  movements: (from: string, to: string) => ['linen', 'movements', from, to] as const,
};

const base = '/api/v1/linen';

export const linenQueries = {
  items: () =>
    queryOptions({
      queryKey: linenKeys.items(),
      queryFn: ({ signal }) => api.get<LinenItem[]>(`${base}/items`, { signal }),
    }),
  wards: () =>
    queryOptions({
      queryKey: linenKeys.wards(),
      queryFn: ({ signal }) => api.get<{ id: string; name: string }[]>(`${base}/wards`, { signal }),
      staleTime: 5 * 60_000,
    }),
  stock: () =>
    queryOptions({
      queryKey: linenKeys.stock(),
      queryFn: ({ signal }) => api.get<LinenStock>(`${base}/stock`, { signal }),
    }),
  movements: (from: string, to: string) =>
    queryOptions({
      queryKey: linenKeys.movements(from, to),
      queryFn: ({ signal }) => api.get<LinenMovement[]>(`${base}/movements`, { query: { from, to }, signal }),
    }),
};

export const linenApi = {
  saveItem: (item: {
    id: string | null;
    code: string;
    name: string;
    lifeWashes: number;
    isActive: boolean;
    version: number | null;
  }) => api.post<LinenItem[]>(`${base}/items`, item),
  setPar: (wardId: string, lines: LinenLine[]) => api.post<LinenStock>(`${base}/par`, { wardId, lines }),
  record: (request: RecordLinenRequest) => api.post<LinenMovement>(`${base}/movements`, request),
};

export const LINEN_PERMISSIONS = {
  /** Send soiled linen and report losses for a ward (nurses). */
  ward: 'linen.ward',
  /** The linen store and the laundry: issue, receive, wash, condemn, par levels (store keepers, administration). */
  manage: 'linen.manage',
} as const;

export const LINEN_READERS = [LINEN_PERMISSIONS.ward, LINEN_PERMISSIONS.manage] as const;

/** Whether a wash batch disinfects, as the server checks: 71 °C held 3 minutes, or 65 °C held 10. */
export function disinfects(temperatureC: number | null, holdMinutes: number | null): boolean {
  if (temperatureC === null || holdMinutes === null) {
    return false;
  }
  return (temperatureC >= 71 && holdMinutes >= 3) || (temperatureC >= 65 && holdMinutes >= 10);
}

/** The counted lines, leaving out items not counted (blank or 0). */
export function countedLines(counts: Record<string, string>): LinenLine[] {
  return Object.entries(counts)
    .map(([linenItemId, value]) => ({ linenItemId, quantity: Number(value) }))
    .filter((l) => Number.isInteger(l.quantity) && l.quantity > 0);
}
