import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Downtime UHIDs (NABH IMS, continuity when HC360 is down): blocks of UHIDs issued ahead of time for the registration
 * desks' downtime kits. Mirrors `DowntimeUhidEndpoints` (`/api/v1/patients/downtime-blocks`, permission
 * `patients.downtime`). A form filled in on paper is entered later through the ordinary registration with `paper`.
 */

export const DOWNTIME_PERMISSION = 'patients.downtime';

/** Issue between 1 and this many UHIDs in one block (`IssueDowntimeUhidsRequestValidator.MaxCount`). */
export const MAX_BLOCK = 200;

/** Open: not used yet, or used on paper and not entered yet (HC360 cannot tell which). */
export type DowntimeUhidStatus = 'Open' | 'Entered' | 'Voided';

export interface DowntimeUhidBlockSummary {
  id: string;
  label: string;
  count: number;
  firstUhid: string;
  lastUhid: string;
  issuedAt: string;
  issuedByName: string | null;
  entered: number;
  voided: number;
  open: number;
}

export interface DowntimeUhidDto {
  position: number;
  uhid: string;
  status: DowntimeUhidStatus;
  patientId: string | null;
  patientName: string | null;
  /** When the paper form says the patient was registered. */
  paperRegisteredAt: string | null;
  enteredAt: string | null;
  enteredByName: string | null;
  voidedAt: string | null;
  voidReason: string | null;
  voidedByName: string | null;
  version: number;
}

export interface DowntimeUhidBlockDetail {
  block: DowntimeUhidBlockSummary;
  facilityName: string;
  numbers: DowntimeUhidDto[];
}

export interface IssueDowntimeUhidsRequest {
  label: string;
  count: number;
}

export interface VoidDowntimeUhidRequest {
  uhid: string;
  reason: string;
  version: number;
}

/** A registration entered from the paper form (`RegisterPatientRequest.paper`). */
export interface PaperRegistration {
  uhid: string;
  registeredAt: string;
}

const BASE = '/api/v1/patients/downtime-blocks';

export const downtimeKeys = {
  all: ['patients', 'downtime-blocks'] as const,
  block: (id: string) => ['patients', 'downtime-blocks', id] as const,
};

export const downtimeQueries = {
  list: () =>
    queryOptions({
      queryKey: downtimeKeys.all,
      queryFn: ({ signal }) => api.get<DowntimeUhidBlockSummary[]>(BASE, { signal }),
    }),
  block: (id: string) =>
    queryOptions({
      queryKey: downtimeKeys.block(id),
      queryFn: ({ signal }) => api.get<DowntimeUhidBlockDetail>(`${BASE}/${id}`, { signal }),
    }),
};

export const downtimeApi = {
  issue: (request: IssueDowntimeUhidsRequest) => api.post<DowntimeUhidBlockDetail>(BASE, request),
  void: (blockId: string, request: VoidDowntimeUhidRequest) => api.post<void>(`${BASE}/${blockId}/void`, request),
};

export const STATUS_LABELS: Record<DowntimeUhidStatus, string> = {
  Open: 'Not entered',
  Entered: 'Entered',
  Voided: 'Voided',
};

/** "12 of 25 entered, 1 voided": how far a block's back-entry has got. */
export function progressText(block: Pick<DowntimeUhidBlockSummary, 'count' | 'entered' | 'voided'>): string {
  const voided = block.voided > 0 ? `, ${block.voided} voided` : '';
  return `${block.entered} of ${block.count} entered${voided}`;
}

/**
 * The time on a paper form, typed as the browser's local date and time (`datetime-local`), as the UTC instant the
 * server takes; null when it is not a whole date and time.
 */
export function paperTimeToIso(local: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(local)) {
    return null;
  }

  const at = new Date(local);
  return Number.isNaN(at.getTime()) ? null : at.toISOString();
}

/** The registration desk's entry of a paper form: off for a registration made now. */
export interface PaperEntry {
  onPaper: boolean;
  uhid: string;
  /** The date and time on the form, as `datetime-local` gives it. */
  at: string;
}

export const NOT_ON_PAPER: PaperEntry = { onPaper: false, uhid: '', at: '' };

/** The paper registration the request carries, null for one made now; `problem` says what is missing. */
export function paperOf(entry: PaperEntry): { paper: PaperRegistration | null; problem: string | null } {
  if (!entry.onPaper) {
    return { paper: null, problem: null };
  }

  const uhid = entry.uhid.trim().toUpperCase();
  const registeredAt = paperTimeToIso(entry.at);
  if (!uhid) {
    return { paper: null, problem: 'Enter the UHID written on the paper form.' };
  }

  if (!registeredAt) {
    return { paper: null, problem: 'Enter the date and time written on the paper form.' };
  }

  return { paper: { uhid, registeredAt }, problem: null };
}
