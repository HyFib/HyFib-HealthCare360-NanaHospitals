import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/** The cold-chain temperature log (docs/modules/supplychain.md, Cold chain). */
const BASE = '/api/v1/inventory/cold-chain';

export const COLD_UNIT_KINDS = ['Refrigerator', 'Freezer', 'ColdRoom'] as const;
export type ColdUnitKind = (typeof COLD_UNIT_KINDS)[number];
export const coldUnitKindLabel: Record<ColdUnitKind, string> = {
  Refrigerator: 'Refrigerator (2–8 °C)',
  Freezer: 'Freezer (−25 to −15 °C)',
  ColdRoom: 'Cold room (2–8 °C)',
};

export type ColdChainCheckStatus = 'Upcoming' | 'Due' | 'Taken' | 'Missed';
export type ColdChainSlot = 'Morning' | 'Evening';
export type TemperatureSource = 'Manual' | 'Logger';

export interface ColdStorageUnitDto {
  id: string;
  storeId: string;
  storeName: string;
  code: string;
  name: string;
  kind: ColdUnitKind;
  minCelsius: number;
  maxCelsius: number;
  isActive: boolean;
  hasLogger: boolean;
  loggerKeyIssuedAt: string | null;
  lastReadingAt: string | null;
  lastCelsius: number | null;
  lastOutOfRange: boolean;
  openExcursionId: string | null;
  lastLoggerReadingAt: string | null;
  morningCheck: ColdChainCheckStatus;
  eveningCheck: ColdChainCheckStatus;
  version: number;
}

export interface SaveColdStorageUnitRequest {
  storeId: string;
  code: string;
  name: string;
  kind: ColdUnitKind;
  minCelsius: number | null;
  maxCelsius: number | null;
  isActive: boolean;
  version: number | null;
}

export interface TemperatureReadingDto {
  id: string;
  takenAt: string;
  celsius: number;
  source: TemperatureSource;
  outOfRange: boolean;
  minCelsius: number;
  maxCelsius: number;
  recordedBy: string | null;
  note: string | null;
}

export interface ColdChainExcursionDto {
  id: string;
  unitId: string;
  startedAt: string;
  endedAt: string | null;
  lowestCelsius: number;
  highestCelsius: number;
  readingsOutOfRange: number;
  minCelsius: number;
  maxCelsius: number;
  extreme: string;
  itemsAtRisk: string | null;
  actionTaken: string | null;
  actionBy: string | null;
  actionAt: string | null;
}

export interface ColdChainCheck {
  slot: ColdChainSlot;
  status: ColdChainCheckStatus;
  takenAt: string | null;
  celsius: number | null;
  recordedBy: string | null;
}

export interface ColdChainDailyRegister {
  unitId: string;
  code: string;
  name: string;
  storeName: string;
  kind: ColdUnitKind;
  date: string;
  minCelsius: number;
  maxCelsius: number;
  lowestCelsius: number | null;
  highestCelsius: number | null;
  checks: ColdChainCheck[];
  readings: TemperatureReadingDto[];
  excursions: ColdChainExcursionDto[];
}

export interface TemperatureReadingRecorded {
  id: string;
  celsius: number;
  outOfRange: boolean;
  allowedRange: string;
  excursionId: string | null;
}

export interface ColdChainLoggerKey {
  unitId: string;
  key: string;
  path: string;
  header: string;
}

export const coldChainKeys = {
  all: ['inventory', 'cold-chain'] as const,
};

export const coldChainQueries = {
  units: (storeId?: string) =>
    queryOptions({
      queryKey: [...coldChainKeys.all, 'units', storeId ?? null],
      queryFn: ({ signal }) => api.get<ColdStorageUnitDto[]>(`${BASE}/units`, { query: { storeId }, signal }),
      refetchInterval: 60_000,
    }),
  register: (unitId: string, date: string) =>
    queryOptions({
      queryKey: [...coldChainKeys.all, 'register', unitId, date],
      queryFn: ({ signal }) =>
        api.get<ColdChainDailyRegister>(`${BASE}/units/${unitId}/register`, { query: { date }, signal }),
    }),
  excursions: (storeId?: string, openOnly = true) =>
    queryOptions({
      queryKey: [...coldChainKeys.all, 'excursions', storeId ?? null, openOnly],
      queryFn: ({ signal }) =>
        api.get<ColdChainExcursionDto[]>(`${BASE}/excursions`, { query: { storeId, openOnly }, signal }),
    }),
};

export const coldChainApi = {
  saveUnit: (id: string | null, body: SaveColdStorageUnitRequest) =>
    id ? api.put<void>(`${BASE}/units/${id}`, body) : api.post<{ id: string } | string>(`${BASE}/units`, body),
  record: (unitId: string, body: { celsius: number; takenAt: string | null; note: string | null }) =>
    api.post<TemperatureReadingRecorded>(`${BASE}/units/${unitId}/readings`, body),
  issueLoggerKey: (unitId: string) => api.post<ColdChainLoggerKey>(`${BASE}/units/${unitId}/logger-key`, {}),
  revokeLoggerKey: (unitId: string) => api.post<void>(`${BASE}/units/${unitId}/logger-key/revoke`, {}),
  recordAction: (excursionId: string, action: string) =>
    api.post<void>(`${BASE}/excursions/${excursionId}/action`, { action }),
};

/** "2 to 8 °C", "−25 to −15 °C". */
export function rangeWords(min: number, max: number): string {
  return `${celsius(min, false)} to ${celsius(max)}`;
}

export function celsius(value: number, unit = true): string {
  const text = (Math.round(value * 10) / 10).toString().replace('-', '−');
  return unit ? `${text} °C` : text;
}
