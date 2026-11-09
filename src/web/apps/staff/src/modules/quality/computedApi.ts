import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { IndicatorValueDto } from './api';
import type { IndicatorUnit } from './labels';

/**
 * The NABH indicators HC360 computes from its own records. Mirrors `ComputedIndicatorEndpoints`
 * (`/api/v1/quality/indicators/compute`, `/{id}/compute`, `/{id}/drilldown`; permission `quality.indicators.manage`).
 */

export type MeasureRole = 'Numerator' | 'Denominator' | 'Excluded';
export type ComputeOutcome = 'Stored' | 'KeptHandEntry' | 'NothingToMeasure' | 'SourceUnavailable';

export const ROLE_LABELS: Record<MeasureRole, string> = {
  Numerator: 'Counted',
  Denominator: 'Denominator only',
  Excluded: 'Left out',
};

export const OUTCOME_LABELS: Record<ComputeOutcome, string> = {
  Stored: 'Computed',
  KeptHandEntry: 'Kept the value entered by hand',
  NothingToMeasure: 'Nothing to measure',
  SourceUnavailable: 'Records not available here',
};

export interface DrilldownRecord {
  role: MeasureRole;
  kind: string;
  reference: string;
  uhid: string | null;
  at: string;
  minutes: number | null;
  detail: string | null;
}

export interface IndicatorDrilldown {
  indicatorId: string;
  code: string;
  name: string;
  numeratorDefinition: string;
  denominatorDefinition: string;
  unit: IndicatorUnit;
  periodStart: string;
  periodEnd: string;
  periodLabel: string;
  numerator: number;
  denominator: number;
  value: number | null;
  meetsTarget: boolean | null;
  stored: IndicatorValueDto | null;
  note: string | null;
  unavailable: boolean;
  recordCount: number;
  records: DrilldownRecord[];
}

export interface ComputedIndicator {
  indicatorId: string;
  code: string;
  name: string;
  periodStart: string;
  outcome: ComputeOutcome;
  numerator: number | null;
  denominator: number | null;
  value: number | null;
  meetsTarget: boolean | null;
}

export const computedQueries = {
  drilldown: (id: string, period: string) =>
    queryOptions({
      queryKey: ['quality', 'indicators', 'drilldown', id, period],
      queryFn: ({ signal }) =>
        api.get<IndicatorDrilldown>(`/api/v1/quality/indicators/${id}/drilldown`, { query: { period }, signal }),
    }),
};

export const computedApi = {
  computeAll: (period: string) => api.post<ComputedIndicator[]>('/api/v1/quality/indicators/compute', { period }),
  compute: (id: string, period: string) =>
    api.post<ComputedIndicator[]>(`/api/v1/quality/indicators/${id}/compute`, { period }),
};

/** The first day of the month a number of months back from today (0: this month). */
export function monthStart(back: number, today = new Date()): string {
  const d = new Date(Date.UTC(today.getFullYear(), today.getMonth() - back, 1));
  return d.toISOString().slice(0, 10);
}

/** "Oct 2026" for the month starting on an ISO date. */
export function monthLabel(iso: string): string {
  return new Date(`${iso}T00:00:00Z`).toLocaleDateString('en-IN', { month: 'short', year: 'numeric', timeZone: 'UTC' });
}
