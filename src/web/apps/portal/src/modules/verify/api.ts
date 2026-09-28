import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { PrescriptionVerification, ReportAuthenticity } from '@/lib/types';
import type { VerifyKind } from './verification';

const code = (value: string) => encodeURIComponent(value);

const REPORT_ENDPOINT: Record<Exclude<VerifyKind, 'prescription'>, string> = {
  lab: '/api/v1/lab/reports/verify/',
  radiology: '/api/v1/radiology/reports/verify/',
  cardiology: '/api/v1/cardiology/reports/verify/',
};

export type VerificationAnswer = { kind: 'prescription'; result: PrescriptionVerification } | { kind: 'report'; result: ReportAuthenticity };

/** Anonymous authenticity checks of printed documents (answers carry no patient data). */
export const verifyQueries = {
  check: (kind: VerifyKind, value: string) =>
    queryOptions({
      queryKey: ['public', 'verify', kind, value],
      queryFn: async ({ signal }): Promise<VerificationAnswer> =>
        kind === 'prescription'
          ? { kind, result: await api.get<PrescriptionVerification>(`/api/v1/public/prescriptions/verify/${code(value)}`, { signal }) }
          : { kind: 'report', result: await api.get<ReportAuthenticity>(`${REPORT_ENDPOINT[kind]}${code(value)}`, { signal }) },
      retry: false,
      staleTime: 60_000,
    }),
};
