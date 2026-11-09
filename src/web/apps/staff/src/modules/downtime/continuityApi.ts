import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Downtime continuity (NABH IMS): visit, bill and receipt numbers issued ahead to the counters, the back-entry windows
 * and late back-entry approvals, and the pre-downtime pack. Mirrors `DowntimeNumberEndpoints`
 * (`/api/v1/downtime/...`).
 */

export const DOWNTIME_PERMISSION = 'patients.downtime';
export const BACK_ENTRY_PERMISSION = 'patients.back_entry';
export const PACK_PERMISSION = 'patients.downtime_pack';

/** Issue between 1 and this many numbers in one block (`IssueDowntimeNumbersRequestValidator.MaxCount`). */
export const MAX_NUMBERS = 500;

export type DowntimeNumberKind = 'Visit' | 'OpBill' | 'IpBill' | 'Receipt';
export type DowntimeNumberStatus = 'Open' | 'Entered' | 'Voided';
export type BackEntryRecord =
  'Registration' | 'Visit' | 'Bill' | 'Receipt' | 'Admission' | 'Transfer' | 'ConsultantVisit' | 'NursingChart';

export const KIND_LABELS: Record<DowntimeNumberKind, string> = {
  Visit: 'Visit numbers',
  OpBill: 'OP bill numbers',
  IpBill: 'IP bill numbers',
  Receipt: 'Receipt numbers',
};

export interface DowntimeNumberBlockSummary {
  id: string;
  kind: DowntimeNumberKind;
  kindName: string;
  counter: string;
  count: number;
  firstNumber: string;
  lastNumber: string;
  issuedAt: string;
  issuedByName: string | null;
  entered: number;
  voided: number;
  open: number;
  returnedAt: string | null;
  returnedByName: string | null;
  returnNote: string | null;
  version: number;
}

export interface DowntimeNumberDto {
  position: number;
  number: string;
  status: DowntimeNumberStatus;
  entityType: string | null;
  entityId: string | null;
  patientId: string | null;
  patientName: string | null;
  paperAt: string | null;
  enteredAt: string | null;
  enteredByName: string | null;
  lateBackEntry: boolean;
  voidedAt: string | null;
  voidReason: string | null;
  voidedByName: string | null;
  version: number;
}

export interface DowntimeNumberBlockDetail {
  block: DowntimeNumberBlockSummary;
  facilityName: string;
  numbers: DowntimeNumberDto[];
}

export interface BackEntryWindowDto {
  record: BackEntryRecord;
  name: string;
  settingKey: string;
  hours: number;
  defaultHours: number;
  maxHours: number;
}

export interface LateBackEntryGrantDto {
  id: string;
  record: BackEntryRecord;
  paperFrom: string;
  paperTo: string;
  maxRecords: number;
  used: number;
  requestedByName: string;
  reason: string;
  approvedByName: string | null;
  approvedAt: string;
  expiresAt: string;
  usable: boolean;
}

export interface RequestLateBackEntry {
  record: BackEntryRecord;
  paperFrom: string;
  paperTo: string;
  records: number;
  reason: string;
}

/** A record entered from paper under a downtime number: the number on the paper and the time on it. */
export interface PaperNumber {
  number: string;
  at: string;
}

export interface DowntimePackTable {
  heading: string;
  note: string | null;
  alert: string | null;
  columns: string[];
  rows: string[][];
}

export interface DowntimePackSection {
  key: string;
  title: string;
  note: string | null;
  pagePerTable: boolean;
  tables: DowntimePackTable[];
}

export interface DowntimePack {
  id: string;
  kind: 'Nightly' | 'OnDemand';
  generatedAt: string;
  windowEndsAt: string;
  facilityName: string;
  generatedByName: string | null;
  patients: number;
  sections: DowntimePackSection[];
}

const BLOCKS = '/api/v1/downtime/number-blocks';
const BACK_ENTRY = '/api/v1/downtime/back-entry';
export const PACK = '/api/v1/downtime/pack';
export const LATEST_PACK = `${PACK}/latest`;

export const continuityKeys = {
  blocks: ['downtime', 'number-blocks'] as const,
  block: (id: string) => ['downtime', 'number-blocks', id] as const,
  windows: ['downtime', 'windows'] as const,
  grants: ['downtime', 'late'] as const,
  latestPack: ['downtime', 'pack', 'latest'] as const,
};

export const continuityQueries = {
  blocks: () =>
    queryOptions({
      queryKey: continuityKeys.blocks,
      queryFn: ({ signal }) => api.get<DowntimeNumberBlockSummary[]>(BLOCKS, { signal }),
    }),
  block: (id: string) =>
    queryOptions({
      queryKey: continuityKeys.block(id),
      queryFn: ({ signal }) => api.get<DowntimeNumberBlockDetail>(`${BLOCKS}/${id}`, { signal }),
    }),
  windows: () =>
    queryOptions({
      queryKey: continuityKeys.windows,
      queryFn: ({ signal }) => api.get<BackEntryWindowDto[]>(`${BACK_ENTRY}/windows`, { signal }),
    }),
  grants: () =>
    queryOptions({
      queryKey: continuityKeys.grants,
      queryFn: ({ signal }) => api.get<LateBackEntryGrantDto[]>(`${BACK_ENTRY}/late`, { signal }),
    }),
};

export const continuityApi = {
  issue: (request: { kind: DowntimeNumberKind; counter: string; count: number }) =>
    api.post<DowntimeNumberBlockDetail>(BLOCKS, request),
  void: (blockId: string, request: { number: string; reason: string; version: number }) =>
    api.post<void>(`${BLOCKS}/${blockId}/void`, request),
  return: (blockId: string, request: { note: string | null; version: number }) =>
    api.post<DowntimeNumberBlockDetail>(`${BLOCKS}/${blockId}/return`, request),
  requestLate: (request: RequestLateBackEntry) => api.post<{ id: string }>(`${BACK_ENTRY}/late`, request),
  printPack: () => api.post<DowntimePack>(PACK, {}),
  latestPack: () => api.get<DowntimePack>(LATEST_PACK),
};

/** "36 hours", "3 days": a window in words, as the server writes it on the pack. */
export function windowWords(hours: number): string {
  return hours % 24 === 0 && hours >= 48 ? `${hours / 24} days` : `${hours} ${hours === 1 ? 'hour' : 'hours'}`;
}

/** "2 of 50 entered, 1 voided, returned": how far a block's back-entry has got. */
export function blockProgress(
  block: Pick<DowntimeNumberBlockSummary, 'count' | 'entered' | 'voided' | 'returnedAt'>,
): string {
  const voided = block.voided > 0 ? `, ${block.voided} voided` : '';
  return `${block.entered} of ${block.count} entered${voided}${block.returnedAt ? ', returned' : ''}`;
}

/** The late back-entry form, checked before it is sent; `problem` says what is missing. */
export function lateRequestOf(form: {
  record: BackEntryRecord;
  from: string;
  to: string;
  records: string;
  reason: string;
}): { request: RequestLateBackEntry | null; problem: string | null } {
  const from = localToIso(form.from);
  const to = localToIso(form.to);
  const records = Number.parseInt(form.records, 10);
  if (!from || !to) {
    return { request: null, problem: 'Enter the earliest and latest date and time on the papers.' };
  }
  if (Date.parse(to) < Date.parse(from)) {
    return { request: null, problem: 'The latest time is before the earliest.' };
  }
  if (!Number.isInteger(records) || records < 1 || records > 5000) {
    return { request: null, problem: 'Enter how many records, from 1 to 5000, from the back-entry tally.' };
  }
  if (!form.reason.trim()) {
    return { request: null, problem: 'Say why the records are entered late.' };
  }
  return {
    request: { record: form.record, paperFrom: from, paperTo: to, records, reason: form.reason.trim() },
    problem: null,
  };
}

function localToIso(local: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(local)) {
    return null;
  }
  const at = new Date(local);
  return Number.isNaN(at.getTime()) ? null : at.toISOString();
}
