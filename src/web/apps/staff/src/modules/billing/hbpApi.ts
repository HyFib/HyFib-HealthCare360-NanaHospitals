import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/** AB PM-JAY's Health Benefit Package master: /api/v1/billing/hbp (INS-PMJAY). */

export interface HbpProcedure {
  code: string;
  packageCode: string;
  specialtyCode: string;
  specialty: string | null;
  packageName: string | null;
  procedureName: string;
  rate: number | null;
  lengthOfStayDays: number | null;
  reservedForPublic: boolean;
  stratification: string | null;
  masterVersion: string;
  isActive: boolean;
}

export interface HbpImportResult {
  masterVersion: string;
  read: number;
  added: number;
  updated: number;
  retired: number;
  problems: string[];
}

const BASE = '/api/v1/billing/hbp';

/** Two letters for the specialty, three digits for the package, a letter for a stratified procedure (BM001A). */
export const HBP_CODE = /^[A-Z]{2}\d{3}[A-Z]?$/;

export const hbpKeys = {
  all: ['billing', 'hbp'] as const,
};

export const hbpQueries = {
  search: (search: string, includeRetired = false) =>
    queryOptions({
      queryKey: [...hbpKeys.all, 'search', search, includeRetired],
      queryFn: ({ signal }) =>
        api.get<HbpProcedure[]>(`${BASE}/procedures`, {
          query: { search: search || undefined, includeRetired },
          signal,
        }),
    }),
  procedure: (code: string) =>
    queryOptions({
      queryKey: [...hbpKeys.all, 'procedure', code],
      queryFn: ({ signal }) => api.get<HbpProcedure>(`${BASE}/procedures/${encodeURIComponent(code)}`, { signal }),
      retry: false,
    }),
};

export const hbpApi = {
  /** Uploads NHA's HBP master (.xlsx or CSV) under its version name. */
  importMaster: (file: File, version: string) => {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('version', version);
    return api.post<HbpImportResult>(`${BASE}/imports`, form, { timeoutMs: 5 * 60_000 });
  },
};
