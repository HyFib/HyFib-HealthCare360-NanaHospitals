import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { TallyExportStatus } from './labels';

/** Tally day book: /api/v1/billing/tally (INT-TALLY, business side). */

export interface TallyExportDto {
  id: string;
  date: string;
  status: TallyExportStatus;
  voucherCount: number;
  attempts: number;
  createdCount: number;
  alteredCount: number;
  errorCount: number;
  createdAt: string;
  lastAttemptAt: string | null;
  postedAt: string | null;
  message: string | null;
}

export interface AccountingEntry {
  ledgerName: string;
  amount: number;
  isDebit: boolean;
  costCentre: string | null;
}

export interface AccountingVoucher {
  voucherType: string;
  date: string;
  number: string;
  narration: string;
  entries: AccountingEntry[];
  reference: string | null;
}

export const tallyQueries = {
  exports: (from: string, to: string) =>
    queryOptions({
      queryKey: ['billing', 'tally', 'exports', from, to],
      queryFn: ({ signal }) => api.get<TallyExportDto[]>('/api/v1/billing/tally/exports', { query: { from, to }, signal }),
      refetchInterval: 30_000,
    }),
  dayBook: (date: string) =>
    queryOptions({
      queryKey: ['billing', 'tally', 'day-book', date],
      queryFn: ({ signal }) => api.get<AccountingVoucher[]>('/api/v1/billing/tally/day-book', { query: { date }, signal }),
    }),
};

export const tallyApi = {
  queue: (date: string) => api.post<void>('/api/v1/billing/tally/exports', { date }),
  exportFile: (date: string) => api.blob('/api/v1/billing/tally/export-file', { query: { date }, timeoutMs: 120_000 }),
};
