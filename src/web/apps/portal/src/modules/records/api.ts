import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DischargeSummaryDto, PortalHealthRecord, PortalRecordRequest, RecordRequestCreated } from '@/lib/types';

/** The hospital's official PDF of a discharge summary. */
export function dischargeSummaryPdfPath(summaryId: string): string {
  return `/api/v1/portal/discharge-summaries/${encodeURIComponent(summaryId)}/pdf`;
}

export const recordQueries = {
  summary: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'records', 'summary'],
      queryFn: ({ signal }) =>
        api.get<PortalHealthRecord>('/api/v1/portal/health-records', { query: { patientId }, signal }),
    }),

  discharge: (patientId: string, summaryId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'records', 'discharge', summaryId],
      queryFn: ({ signal }) =>
        api.get<DischargeSummaryDto>(`/api/v1/portal/discharge-summaries/${encodeURIComponent(summaryId)}`, { signal }),
      staleTime: 5 * 60_000,
    }),
};

export const recordApi = {
  requestCopy: (request: PortalRecordRequest) =>
    api.post<RecordRequestCreated>('/api/v1/portal/record-requests', request),
};
