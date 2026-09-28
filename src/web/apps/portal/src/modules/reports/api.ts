import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { CardiologyReportView, LabReportView, PortalReportItem, RadiologyReportView } from '@/lib/types';

/** URL segment of each report kind (short, readable, and never patient data). */
export const REPORT_KINDS = ['lab', 'radiology', 'cardiology'] as const;
export type ReportKindSegment = (typeof REPORT_KINDS)[number];

export function kindSegment(kind: PortalReportItem['kind']): ReportKindSegment {
  return kind === 'Laboratory' ? 'lab' : kind === 'Radiology' ? 'radiology' : 'cardiology';
}

export function isReportKind(value: unknown): value is ReportKindSegment {
  return typeof value === 'string' && (REPORT_KINDS as readonly string[]).includes(value);
}

const id = (value: string) => encodeURIComponent(value);

export const reportQueries = {
  list: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'reports'],
      queryFn: ({ signal }) => api.get<PortalReportItem[]>('/api/v1/portal/reports', { query: { patientId }, signal }),
    }),

  lab: (patientId: string, reportId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'reports', 'lab', reportId],
      queryFn: ({ signal }) => api.get<LabReportView>(`/api/v1/portal/reports/lab/${id(reportId)}`, { signal }),
      staleTime: 5 * 60_000,
    }),

  radiology: (patientId: string, studyId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'reports', 'radiology', studyId],
      queryFn: ({ signal }) => api.get<RadiologyReportView>(`/api/v1/portal/reports/radiology/${id(studyId)}`, { signal }),
      staleTime: 5 * 60_000,
    }),

  cardiology: (patientId: string, studyId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'reports', 'cardiology', studyId],
      queryFn: ({ signal }) => api.get<CardiologyReportView>(`/api/v1/portal/reports/cardiology/${id(studyId)}`, { signal }),
      staleTime: 5 * 60_000,
    }),
};
