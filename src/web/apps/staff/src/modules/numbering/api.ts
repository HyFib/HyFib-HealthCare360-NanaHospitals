import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Number formats (Hospital setup): how UHIDs, bills, receipts, accession numbers and the other numbers HC360 issues
 * look. Mirrors `NumberSeriesEndpoints` (`/api/v1/admin/number-series`, permission `admin.numbering.manage`). Every
 * series a module issues is listed, including those that have not issued a number yet, so formats can be set before
 * go-live. A change applies to numbers issued afterwards.
 */

export const NUMBERING_PERMISSION = 'admin.numbering.manage';

export type NumberResetPolicy = 'Never' | 'Yearly' | 'Monthly' | 'Daily';

export const RESET_POLICIES: readonly NumberResetPolicy[] = ['Never', 'Yearly', 'Monthly', 'Daily'];

/** One series for the signed-in user's facility, or shared by all facilities (`scope` "global"). */
export interface NumberSeriesItem {
  key: string;
  /** What staff call these numbers: "OP bill", "UHID (patient number)". */
  name: string;
  /** The part of HC360 that issues them, as its menu names it: "Billing", "Laboratory". */
  area: string;
  scope: 'facility' | 'global';
  format: string;
  resetPolicy: NumberResetPolicy;
  /** The running number last issued in the current period (this year, this month…); 0 if none yet. */
  lastNumber: number;
  /** The number the next allocation would make now. */
  nextExample: string;
  /** What {FAC} stands for in these numbers. */
  facilityCode: string | null;
  isCustomised: boolean;
  isLocked: boolean;
  /** Why the format is fixed (a sentence), for a locked series. */
  lockedReason: string | null;
  /** The longest number the column holds. */
  maxLength: number;
  defaultFormat: string;
  defaultResetPolicy: NumberResetPolicy;
}

export interface NumberSeriesList {
  /** More than one: numbers counted per facility need {FAC}. */
  facilityCount: number;
  /** The length of the longest facility code in use (at most 6). */
  longestFacilityCode: number;
  series: NumberSeriesItem[];
}

/** `expectedFormat`/`expectedResetPolicy` are the values the change started from (409 `numbering.changed` otherwise). */
export interface SaveNumberSeriesRequest {
  format: string;
  resetPolicy: NumberResetPolicy;
  /** The next number to issue; null carries on counting. */
  startAt: number | null;
  expectedFormat: string;
  expectedResetPolicy: NumberResetPolicy;
}

const BASE = '/api/v1/admin/number-series';

export const numberingKeys = { all: ['numbering', 'series'] as const };

export const numberingQueries = {
  list: () =>
    queryOptions({
      queryKey: numberingKeys.all,
      queryFn: ({ signal }) => api.get<NumberSeriesList>(BASE, { signal }),
    }),
};

export const numberingApi = {
  save: (key: string, request: SaveNumberSeriesRequest) => api.put<void>(`${BASE}/${encodeURIComponent(key)}`, request),
};

export type SeriesSort = 'area' | 'name';

const byText = (a: string, b: string) => a.localeCompare(b, 'en', { sensitivity: 'base' });

/** The areas in the list, in order, for the area filter. */
export function seriesAreas(series: readonly NumberSeriesItem[]): string[] {
  return [...new Set(series.map((s) => s.area))].sort(byText);
}

/** Series in `area` (all when none) whose name, area or format contains `search`. */
export function filterSeries(
  series: readonly NumberSeriesItem[],
  area: string | undefined,
  search: string | undefined,
): NumberSeriesItem[] {
  const term = search?.trim().toLowerCase() ?? '';
  return series.filter(
    (s) =>
      (!area || s.area === area) &&
      (!term ||
        s.name.toLowerCase().includes(term) ||
        s.area.toLowerCase().includes(term) ||
        s.format.toLowerCase().includes(term)),
  );
}

/** By area and then name (as the server lists them), or by name. */
export function sortSeries(series: readonly NumberSeriesItem[], sort: SeriesSort, desc: boolean): NumberSeriesItem[] {
  const sorted = [...series].sort((a, b) =>
    sort === 'name' ? byText(a.name, b.name) : byText(a.area, b.area) || byText(a.name, b.name),
  );
  return desc ? sorted.reverse() : sorted;
}
