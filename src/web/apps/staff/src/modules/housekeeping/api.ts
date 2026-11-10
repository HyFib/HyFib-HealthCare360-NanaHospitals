// Housekeeping beyond the bed turnaround (/api/v1/housekeeping): areas cleaned on a schedule against their checklists,
// the day's board, cleaning asked for by the wards (spill, terminal, routine) with the response time against each
// kind's target, and every cleaning verified by a supervisor who did not do it.
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

export const RISK_ZONES = ['High', 'Moderate', 'Low'] as const;
export type CleaningRiskZone = (typeof RISK_ZONES)[number];
export type CleaningFrequency = 'Daily' | 'Weekly';
export type CleaningStatus = 'AwaitingVerification' | 'Verified' | 'Redo';
export const REQUEST_KINDS = ['Spill', 'Terminal', 'Routine'] as const;
export type CleaningRequestKind = (typeof REQUEST_KINDS)[number];
export type CleaningRequestStatus =
  'Open' | 'Acknowledged' | 'AwaitingVerification' | 'Verified' | 'Redo' | 'Cancelled';
export const WEEK_DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;
export type DayOfWeek = (typeof WEEK_DAYS)[number];

export const RISK_LABEL: Record<CleaningRiskZone, string> = {
  High: 'High risk (OT, ICU, isolation)',
  Moderate: 'Moderate (wards, casualty)',
  Low: 'Low (corridors, offices)',
};

export const KIND_LABEL: Record<CleaningRequestKind, string> = {
  Spill: 'Spill',
  Terminal: 'Terminal cleaning',
  Routine: 'Routine',
};

export const REQUEST_STATUS_LABEL: Record<CleaningRequestStatus, string> = {
  Open: 'Waiting',
  Acknowledged: 'On the way',
  AwaitingVerification: 'Done, to verify',
  Verified: 'Verified',
  Redo: 'Sent back',
  Cancelled: 'Cancelled',
};

export interface ChecklistResult {
  item: string;
  done: boolean;
  note?: string | null;
}

export interface CleaningArea {
  id: string;
  code: string;
  name: string;
  wardId: string | null;
  wardName: string | null;
  riskZone: CleaningRiskZone;
  frequency: CleaningFrequency;
  timesPerDay: number;
  weekDays: DayOfWeek[];
  checklist: string[];
  isActive: boolean;
  version: number;
}

export interface SaveAreaRequest {
  code: string;
  name: string;
  wardId: string | null;
  riskZone: CleaningRiskZone;
  frequency: CleaningFrequency;
  timesPerDay: number;
  weekDays: DayOfWeek[] | null;
  checklist: string[];
  isActive: boolean;
  version: number | null;
}

export interface CleaningRecord {
  id: string;
  areaId: string;
  date: string;
  slot: number;
  cleanedAt: string;
  cleanedByName: string;
  checklist: ChecklistResult[];
  notes: string | null;
  status: CleaningStatus;
  verifiedAt: string | null;
  verifiedByName: string | null;
  verificationRemarks: string | null;
  cleanedByYou: boolean;
  version: number;
}

export interface CleaningSlot {
  areaId: string;
  areaCode: string;
  areaName: string;
  wardName: string | null;
  riskZone: CleaningRiskZone;
  slot: number;
  checklist: string[];
  record: CleaningRecord | null;
}

export interface CleaningBoard {
  date: string;
  slots: CleaningSlot[];
  due: number;
  notDone: number;
  awaitingVerification: number;
  redo: number;
}

export interface CleaningRequest {
  id: string;
  kind: CleaningRequestKind;
  wardId: string | null;
  wardName: string | null;
  location: string;
  details: string | null;
  requestedAt: string;
  requestedByName: string;
  status: CleaningRequestStatus;
  targetMinutes: number;
  acknowledgedAt: string | null;
  acknowledgedByName: string | null;
  responseMinutes: number | null;
  responseLate: boolean;
  completedAt: string | null;
  completedByName: string | null;
  completionMinutes: number | null;
  checklistItems: string[];
  checklist: ChecklistResult[];
  notes: string | null;
  verifiedAt: string | null;
  verifiedByName: string | null;
  verificationRemarks: string | null;
  completedByYou: boolean;
  version: number;
}

export interface HousekeepingReport {
  from: string;
  to: string;
  requests: {
    kind: CleaningRequestKind;
    requests: number;
    targetMinutes: number;
    medianResponseMinutes: number | null;
    late: number;
    sentBack: number;
  }[];
  scheduledDue: number;
  scheduledDone: number;
  scheduledVerified: number;
  sentBack: number;
}

export const HOUSEKEEPING_PERMISSIONS = {
  /** Ask for a spill, terminal or routine cleaning (wards). */
  request: 'housekeeping.request',
  /** Do the cleaning against the checklists. */
  work: 'housekeeping.work',
  /** Verify cleaning someone else did (a supervisor). */
  verify: 'housekeeping.verify',
  /** Set up the areas, their frequency and checklists. */
  manage: 'housekeeping.manage',
} as const;

export const HOUSEKEEPING_READERS = [
  HOUSEKEEPING_PERMISSIONS.request,
  HOUSEKEEPING_PERMISSIONS.work,
  HOUSEKEEPING_PERMISSIONS.verify,
  HOUSEKEEPING_PERMISSIONS.manage,
] as const;

export const housekeepingKeys = {
  all: ['housekeeping'] as const,
  areas: () => ['housekeeping', 'areas'] as const,
  board: (date: string) => ['housekeeping', 'board', date] as const,
  requests: () => ['housekeeping', 'requests'] as const,
  report: () => ['housekeeping', 'report'] as const,
};

const base = '/api/v1/housekeeping';

export const housekeepingQueries = {
  areas: () =>
    queryOptions({
      queryKey: housekeepingKeys.areas(),
      queryFn: ({ signal }) => api.get<CleaningArea[]>(`${base}/areas`, { query: { includeInactive: true }, signal }),
    }),
  board: (date: string) =>
    queryOptions({
      queryKey: housekeepingKeys.board(date),
      queryFn: ({ signal }) => api.get<CleaningBoard>(`${base}/board`, { query: { date }, signal }),
    }),
  requests: () =>
    queryOptions({
      queryKey: housekeepingKeys.requests(),
      queryFn: ({ signal }) => api.get<CleaningRequest[]>(`${base}/requests`, { signal }),
      refetchInterval: 60_000,
    }),
  report: () =>
    queryOptions({
      queryKey: housekeepingKeys.report(),
      queryFn: ({ signal }) => api.get<HousekeepingReport>(`${base}/report`, { signal }),
    }),
};

export const housekeepingApi = {
  saveArea: (id: string | null, body: SaveAreaRequest) =>
    id ? api.put<CleaningArea>(`${base}/areas/${id}`, body) : api.post<CleaningArea>(`${base}/areas`, body),
  record: (areaId: string, body: { date: string; slot: number; checklist: ChecklistResult[]; notes: string | null }) =>
    api.post<CleaningRecord>(`${base}/areas/${areaId}/cleanings`, body),
  verifyRecord: (id: string, passed: boolean, remarks: string | null, version: number) =>
    api.post<CleaningRecord>(`${base}/cleanings/${id}/verify`, { passed, remarks, version }),
  raise: (body: { kind: CleaningRequestKind; wardId: string | null; location: string; details: string | null }) =>
    api.post<CleaningRequest>(`${base}/requests`, body),
  acknowledge: (id: string, version: number) =>
    api.post<CleaningRequest>(`${base}/requests/${id}/acknowledge`, { version }),
  complete: (id: string, checklist: ChecklistResult[], notes: string | null, version: number) =>
    api.post<CleaningRequest>(`${base}/requests/${id}/complete`, { checklist, notes, version }),
  verifyRequest: (id: string, passed: boolean, remarks: string | null, version: number) =>
    api.post<CleaningRequest>(`${base}/requests/${id}/verify`, { passed, remarks, version }),
};

/** What stops a cleaning being recorded, as the server checks it (CleaningRules.CheckList): an item unanswered, or not done without a note. */
export function checklistProblem(items: readonly string[], answers: readonly ChecklistResult[]): string | null {
  const missing = items.filter((item) => !answers.some((a) => a.item === item));
  if (missing.length > 0) {
    return `Answer every item of the checklist: ${missing.join(', ')}.`;
  }

  const unexplained = answers.find((a) => !a.done && !a.note?.trim());
  return unexplained ? `Say why "${unexplained.item}" was not done.` : null;
}
