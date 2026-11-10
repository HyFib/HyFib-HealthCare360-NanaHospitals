import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * External quality assessment (proficiency testing) for NABL 112 and ISO 15189 7.3.7.3: rounds per analyte and scheme,
 * judged by the z-score of ISO 13528 against the provider's assigned value and SD.
 */
export type EqasPerformance = 'Acceptable' | 'Warning' | 'Unacceptable';

export interface EqasSchemeDto {
  id: string;
  name: string;
  provider: string;
  participantCode: string | null;
  roundsPerYear: number;
  isActive: boolean;
  version: number;
}

export interface EqasRoundDto {
  id: string;
  schemeId: string;
  schemeName: string;
  provider: string;
  labTestId: string;
  testCode: string;
  testName: string;
  unit: string | null;
  roundCode: string;
  dueDate: string;
  sampleReceivedOn: string | null;
  reportedValue: number | null;
  reportedOn: string | null;
  assignedValue: number | null;
  assignedSd: number | null;
  peerGroup: string | null;
  evaluatedOn: string | null;
  zScore: number | null;
  performance: EqasPerformance | null;
  isOverdue: boolean;
  needsCapa: boolean;
  capaId: string | null;
  capaNumber: string | null;
  capaStatus: string | null;
  acceptedAt: string | null;
  acceptedByName: string | null;
  acceptanceNote: string | null;
  version: number;
}

export interface EqasSummary {
  overdue: number;
  dueThisWeek: number;
  needCapa: number;
  acceptable: number;
  warning: number;
  unacceptable: number;
  attention: EqasRoundDto[];
}

export interface EqasRoundFilters {
  schemeId?: string;
  performance?: EqasPerformance;
  overdue?: boolean;
  needsCapa?: boolean;
  page?: number;
}

/** What the round needs next, in the order the lab does it. */
export type EqasStep = 'report' | 'evaluate' | 'capa' | 'accept' | 'done';

/** The next step of a round (pure; unit-tested). */
export function nextStep(
  round: Pick<EqasRoundDto, 'reportedValue' | 'performance' | 'needsCapa' | 'acceptedAt'>,
): EqasStep {
  if (round.reportedValue === null) {
    return 'report';
  }
  if (round.performance === null) {
    return 'evaluate';
  }
  if (round.needsCapa) {
    return 'capa';
  }
  return round.acceptedAt === null ? 'accept' : 'done';
}

/** z = (x − X) / σpt to two decimals, as the server computes it, for the preview while the evaluation is typed. */
export function zScore(reported: number, assigned: number, sd: number): number | null {
  if (!(sd > 0) || !Number.isFinite(reported) || !Number.isFinite(assigned)) {
    return null;
  }
  const z = (reported - assigned) / sd;
  return (Math.sign(z) * Math.round(Math.abs(z) * 100 + Number.EPSILON)) / 100;
}

/** The ISO 13528 band: |z| ≤ 2 acceptable, ≤ 3 warning, above unacceptable. */
export function judge(z: number): EqasPerformance {
  const size = Math.abs(z);
  return size > 3 ? 'Unacceptable' : size > 2 ? 'Warning' : 'Acceptable';
}

export const performanceTone: Record<EqasPerformance, 'success' | 'warning' | 'danger'> = {
  Acceptable: 'success',
  Warning: 'warning',
  Unacceptable: 'danger',
};

const PAGE = 50;

export const eqasQueries = {
  schemes: () =>
    queryOptions({
      queryKey: ['lab', 'eqas', 'schemes'],
      queryFn: ({ signal }) => api.get<EqasSchemeDto[]>('/api/v1/lab/eqas/schemes', { signal }),
    }),
  rounds: (filters: EqasRoundFilters) =>
    queryOptions({
      queryKey: ['lab', 'eqas', 'rounds', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<EqasRoundDto>>('/api/v1/lab/eqas/rounds', {
          query: { ...filters, page: filters.page ?? 1, pageSize: PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  summary: () =>
    queryOptions({
      queryKey: ['lab', 'eqas', 'summary'],
      queryFn: ({ signal }) => api.get<EqasSummary>('/api/v1/lab/eqas/summary', { signal }),
    }),
};

export const eqasKeys = { all: ['lab', 'eqas'] as const };

export const eqasApi = {
  saveScheme: (request: Omit<EqasSchemeDto, 'id' | 'version'> & { version: number | null }) =>
    api.post<{ id: string }>('/api/v1/lab/eqas/schemes', request),
  schedule: (request: {
    schemeId: string;
    labTestId: string;
    roundCode: string;
    dueDate: string;
    sampleReceivedOn: string | null;
  }) => api.post<string>('/api/v1/lab/eqas/rounds', request),
  report: (id: string, value: number, reportedOn: string, version: number) =>
    api.put<EqasRoundDto>(`/api/v1/lab/eqas/rounds/${id}/result`, { value, reportedOn, version }),
  evaluate: (
    id: string,
    assignedValue: number,
    assignedSd: number,
    peerGroup: string | null,
    evaluatedOn: string,
    version: number,
  ) =>
    api.put<EqasRoundDto>(`/api/v1/lab/eqas/rounds/${id}/evaluation`, {
      assignedValue,
      assignedSd,
      peerGroup,
      evaluatedOn,
      version,
    }),
  linkCapa: (id: string, capaNumber: string, version: number) =>
    api.put<EqasRoundDto>(`/api/v1/lab/eqas/rounds/${id}/capa`, { capaId: null, capaNumber, version }),
  accept: (id: string, note: string | null, version: number) =>
    api.post<EqasRoundDto>(`/api/v1/lab/eqas/rounds/${id}/accept`, { note, version }),
};
