import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ApprovalRequestDto } from '@/modules/approvals/api';

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

/** Where an import of the master stands: it changes the master only once a second person approves it. */
export type HbpImportStatus = 'Waiting' | 'Applied' | 'Rejected' | 'Withdrawn' | 'Expired' | 'NotApplied';

/** An import of NHA's master: the file (and its SHA-256), what it reads and changes, who asked and who decided. */
export interface HbpImport {
  id: string;
  masterVersion: string;
  fileName: string;
  fileSha256: string;
  read: number;
  added: number;
  updated: number;
  retired: number;
  problemCount: number;
  problems: string[];
  status: HbpImportStatus;
  askedBy: string;
  askedAt: string;
  decidedBy: string | null;
  decidedAt: string | null;
  /** The approver's reason for refusing it, or why it could not be applied. */
  note: string | null;
  approvalId: string | null;
}

/** An import staged, and the approval it waits for. */
export interface HbpImportAsked {
  import: HbpImport;
  approval: ApprovalRequestDto;
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
  /** The latest imports, newest first: the master's history and an import still waiting. */
  imports: () =>
    queryOptions({
      queryKey: [...hbpKeys.all, 'imports'],
      queryFn: ({ signal }) => api.get<HbpImport[]>(`${BASE}/imports`, { signal }),
    }),
  procedure: (code: string) =>
    queryOptions({
      queryKey: [...hbpKeys.all, 'procedure', code],
      queryFn: ({ signal }) => api.get<HbpProcedure>(`${BASE}/procedures/${encodeURIComponent(code)}`, { signal }),
      retry: false,
    }),
};

export const hbpApi = {
  /**
   * Uploads NHA's HBP master (.xlsx or CSV) under its version name, with the reason for the approver: the master is
   * not changed until a second person approves the import.
   */
  importMaster: (file: File, version: string, reason: string) => {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('version', version);
    if (reason) {
      form.append('reason', reason);
    }
    return api.post<HbpImportAsked>(`${BASE}/imports`, form, { timeoutMs: 5 * 60_000 });
  },
};
