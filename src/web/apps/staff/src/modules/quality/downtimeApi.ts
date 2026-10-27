import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * The downtime log (NABH IMS: downtime monitored and its procedure rehearsed). Mirrors `DowntimeEndpoints`
 * (`/api/v1/quality/downtime`, permission `quality.downtime.manage`).
 */

export const DOWNTIME_PERMISSION = 'quality.downtime.manage';

export type DowntimeKind = 'Unplanned' | 'Planned' | 'Drill';
export type DowntimeCause =
  'ServerFailure' | 'NetworkOutage' | 'PowerFailure' | 'SoftwareFault' | 'CyberIncident' | 'Maintenance' | 'Other';
export type DowntimeStatus = 'Ongoing' | 'BackEntryPending' | 'Closed';

export const DOWNTIME_KINDS: readonly DowntimeKind[] = ['Unplanned', 'Planned', 'Drill'];
export const DOWNTIME_CAUSES: readonly DowntimeCause[] = [
  'ServerFailure',
  'NetworkOutage',
  'PowerFailure',
  'SoftwareFault',
  'CyberIncident',
  'Maintenance',
  'Other',
];

export const KIND_LABELS: Record<DowntimeKind, string> = {
  Unplanned: 'Unplanned',
  Planned: 'Planned',
  Drill: 'Drill',
};

export const CAUSE_LABELS: Record<DowntimeCause, string> = {
  ServerFailure: 'Server failure',
  NetworkOutage: 'Network outage',
  PowerFailure: 'Power failure',
  SoftwareFault: 'Software fault',
  CyberIncident: 'Cyber incident',
  Maintenance: 'Maintenance or upgrade',
  Other: 'Other',
};

export const STATUS_LABELS: Record<DowntimeStatus, string> = {
  Ongoing: 'Ongoing',
  BackEntryPending: 'Back-entry pending',
  Closed: 'Closed',
};

export interface DowntimeEvent {
  id: string;
  number: string;
  kind: DowntimeKind;
  cause: DowntimeCause;
  affected: string;
  description: string | null;
  startedAt: string;
  endedAt: string | null;
  minutes: number;
  status: DowntimeStatus;
  recordedByName: string | null;
  actionsTaken: string | null;
  endedByName: string | null;
  backEntryCompletedAt: string | null;
  paperRecords: number | null;
  backEntryNote: string | null;
  backEntrySignedByName: string | null;
  version: number;
}

export interface DowntimeSummary {
  from: string;
  to: string;
  unplanned: number;
  planned: number;
  drills: number;
  unplannedMinutes: number;
  plannedMinutes: number;
  longestUnplannedMinutes: number;
  unplannedAvailabilityPercent: number;
  meanMinutesToRestore: number | null;
  meanHoursToBackEntry: number | null;
  ongoing: number;
  backEntryPending: number;
  lastDrillAt: string | null;
}

export interface RecordDowntimeRequest {
  kind: DowntimeKind;
  cause: DowntimeCause;
  affected: string;
  description: string | null;
  startedAt: string;
  endedAt: string | null;
  actionsTaken: string | null;
}

export interface EndDowntimeRequest {
  endedAt: string;
  actionsTaken: string | null;
  version: number;
}

export interface SignOffBackEntryRequest {
  paperRecords: number;
  note: string | null;
  version: number;
}

const BASE = '/api/v1/quality/downtime';

export const downtimeKeys = { all: ['quality', 'downtime'] as const };

export const downtimeQueries = {
  list: () =>
    queryOptions({
      queryKey: [...downtimeKeys.all, 'list'],
      queryFn: ({ signal }) => api.get<DowntimeEvent[]>(BASE, { signal }),
    }),
  summary: () =>
    queryOptions({
      queryKey: [...downtimeKeys.all, 'summary'],
      queryFn: ({ signal }) => api.get<DowntimeSummary>(`${BASE}/summary`, { signal }),
    }),
};

export const downtimeApi = {
  record: (request: RecordDowntimeRequest) => api.post<DowntimeEvent>(BASE, request),
  end: (id: string, request: EndDowntimeRequest) => api.post<DowntimeEvent>(`${BASE}/${id}/end`, request),
  signOff: (id: string, request: SignOffBackEntryRequest) =>
    api.post<DowntimeEvent>(`${BASE}/${id}/back-entry`, request),
};

/** "2 h 15 min", "45 min": how long a downtime lasted. */
export function durationText(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours === 0) {
    return `${rest} min`;
  }

  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

/** A `datetime-local` value (the browser's local time) as the UTC instant the server takes; null when incomplete. */
export function localToIso(local: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(local)) {
    return null;
  }

  const at = new Date(local);
  return Number.isNaN(at.getTime()) ? null : at.toISOString();
}

/** The instant as a `datetime-local` value in the browser's local time, to the minute. */
export function isoToLocal(at: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}T${pad(at.getHours())}:${pad(at.getMinutes())}`;
}
