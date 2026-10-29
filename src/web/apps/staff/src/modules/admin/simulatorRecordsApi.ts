import { formatDateTime, formatInr } from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/** One kind of record a simulator leaves behind, and what the latest sweep found of it. */
export interface SimulatorRecordFinding {
  code: string;
  title: string;
  severity: 'critical' | 'warning' | 'info';
  count: number;
  /** The money the records say moved (₹), for payments and refunds. */
  amount: number | null;
  first: string | null;
  last: string | null;
  /** Up to five of the newest records' references, to find them by. */
  examples: string[];
  /** What to do about them. */
  guidance: string;
}

export interface SimulatorSweep {
  sweptAt: string;
  sweptBy: string | null;
  findings: SimulatorRecordFinding[];
  total: number;
  newest: string | null;
}

export interface SimulatorSignOff {
  signedOffAt: string;
  signedOffBy: string | null;
  sweptAt: string;
  coversUntil: string | null;
  total: number;
  note: string;
}

export interface SimulatorRecordsView {
  latest: SimulatorSweep | null;
  signedOff: SimulatorSignOff | null;
  /** The latest sweep found records no sign-off covers. */
  outstanding: boolean;
  /** This server may not use the simulators, so their records do not belong here. */
  liveServer: boolean;
}

const RECORDS = '/api/v1/admin/system/simulator-records';

export const simulatorRecordsQueries = {
  view: () =>
    queryOptions({
      queryKey: ['admin', 'simulator-records'],
      queryFn: ({ signal }) => api.get<SimulatorRecordsView>(RECORDS, { signal }),
    }),
};

export const sweepSimulatorRecords = () => api.post<SimulatorSweep>(`${RECORDS}/sweep`);

export const signOffSimulatorRecords = (sweptAt: string, note: string) =>
  api.post<SimulatorSignOff>(`${RECORDS}/sign-off`, { sweptAt, note });

/** What the screen says first: whether anything waits for the IT officer, and since when it was clear. */
export function sweepStatus(view: SimulatorRecordsView): {
  intent: 'success' | 'warning' | 'info';
  title: string;
  text: string;
} {
  const { latest, signedOff } = view;
  if (!latest) {
    return {
      intent: 'info',
      title: 'Not swept yet',
      text: view.liveServer
        ? 'A live server sweeps its database every day. Sweep now to see what is there.'
        : 'A training server may use the simulators. Sweep before this database goes live.',
    };
  }

  const swept = `Swept ${formatDateTime(latest.sweptAt)}${latest.sweptBy ? ` by ${latest.sweptBy}` : ''}.`;
  if (latest.total === 0) {
    return { intent: 'success', title: 'No records made by simulators', text: swept };
  }

  if (!view.outstanding && signedOff) {
    return {
      intent: 'success',
      title: 'Signed off',
      text: `${swept} ${signedOff.signedOffBy ?? 'The IT officer'} signed them off ${formatDateTime(signedOff.signedOffAt)}: ${signedOff.note}`,
    };
  }

  return {
    intent: view.liveServer ? 'warning' : 'info',
    title: `${latest.total.toLocaleString('en-IN')} ${latest.total === 1 ? 'record' : 'records'} made by simulators, not signed off`,
    text: view.liveServer
      ? `${swept} They look real but are not: deal with each kind as it says, then sign them off.`
      : `${swept} This training server may use the simulators: sign them off once the database is to go live.`,
  };
}

/** How many a kind found, and the money they say moved. */
export function foundText(finding: SimulatorRecordFinding): string {
  const count = finding.count.toLocaleString('en-IN');
  return finding.amount ? `${count}, ${formatInr(finding.amount)}` : count;
}

/** When the records of a kind were made: one day, or the first and the last. */
export function whenText(finding: SimulatorRecordFinding): string {
  if (!finding.first || !finding.last) {
    return '—';
  }
  return finding.first === finding.last
    ? formatDateTime(finding.first)
    : `${formatDateTime(finding.first)} to ${formatDateTime(finding.last)}`;
}
