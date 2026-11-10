import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/** The kinds of record with a history, as the server and its audit trail name them. */
export type HistoryRecordType = 'Patient' | 'Visit' | 'Bill' | 'LabOrder' | 'LabReport' | 'Admission';

/** One field's change: what it was and what it became, as the server words it. Masked values stay masked ("***"). */
export interface HistoryChange {
  field: string;
  from: string | null;
  to: string | null;
}

/**
 * One entry of a record's history. `action` is the audit trail's own (`Bill.update`, `lab.report.amended`), `part` names
 * the part of the record it is about (a report's result, a bill's receipt, an approval asked for on it), null for the
 * record itself, `what` says it in words, and `changes` lists the fields a data change set, changed or removed.
 */
export interface HistoryEntry {
  id: number;
  at: string;
  by: string | null;
  action: string;
  part: string | null;
  what: string;
  changes: HistoryChange[];
}

export interface RecordHistoryDto {
  entityType: HistoryRecordType;
  entityId: string;
  title: string;
  entries: PagedResult<HistoryEntry>;
}

export const historyKeys = {
  all: ['history'] as const,
  record: (type: HistoryRecordType, id: string, page: number) => ['history', type, id, page] as const,
};

export const historyQueries = {
  /** The record's history, newest first, 25 entries a page; reading it is in the patient's access log. */
  record: (type: HistoryRecordType, id: string, page: number) =>
    queryOptions({
      queryKey: historyKeys.record(type, id, page),
      queryFn: ({ signal }) =>
        api.get<RecordHistoryDto>(`/api/v1/history/${type}/${id}`, { query: { page, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
};
