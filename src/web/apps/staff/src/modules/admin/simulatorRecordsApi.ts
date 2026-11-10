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
  /**
   * How many of them their current state does not show corrected yet, for the kinds a sign-off needs corrected (the
   * critical ones); null for the kinds the IT officer acknowledges.
   */
  open: number | null;
  /** The money still recorded as moved by the open ones (₹). */
  openAmount: number | null;
}

/** A record a simulator made, not corrected yet, and the record it is corrected on. */
export interface SimulatorOpenRecord {
  reference: string;
  at: string | null;
  amount: number | null;
  entity: 'Receipt' | 'Refund' | 'Patient' | 'ImagingStudy' | 'LabOrder' | 'UnmatchedLabResult';
  entityId: string;
  patientId: string | null;
}

export interface SimulatorSweep {
  sweptAt: string;
  sweptBy: string | null;
  findings: SimulatorRecordFinding[];
  total: number;
  newest: string | null;
  /** The critical records not corrected yet: no sign-off is taken while any stands. */
  criticalOpen: number;
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
  /** The records of a kind still open now, newest first: the IT officer's worklist. */
  open: (code: string) =>
    queryOptions({
      queryKey: ['admin', 'simulator-records', 'open', code],
      queryFn: ({ signal }) =>
        api.get<SimulatorOpenRecord[]>(`${RECORDS}/${encodeURIComponent(code)}/open`, { signal }),
    }),
};

/** The hospital's day (YYYY-MM-DD, India time) a record was made on, as the lists filter by. */
const hospitalDay = (at: string) => new Date(at).toLocaleDateString('en-CA', { timeZone: 'Asia/Kolkata' });

/** Where an open record is corrected, and what the link says. */
export function openRecordLink(record: SimulatorOpenRecord): {
  label: string;
  to: string;
  search?: Record<string, string>;
} {
  switch (record.entity) {
    case 'Receipt': {
      // The receipts list shows one day at a time: the receipt's own.
      const day = record.at ? hospitalDay(record.at) : undefined;
      return {
        label: 'Open the receipt',
        to: '/billing/receipts',
        search: day ? { q: record.reference, from: day, to: day } : { q: record.reference },
      };
    }
    case 'Refund':
      return { label: 'Open the refund', to: `/billing/refunds/${record.entityId}` };
    case 'Patient':
      return { label: "Open the patient's record", to: `/patients/${record.entityId}` };
    case 'ImagingStudy':
      return { label: 'Open the study', to: `/radiology/studies/${record.entityId}` };
    case 'LabOrder':
      return { label: 'Open the lab order', to: `/lab/orders/${record.entityId}` };
    case 'UnmatchedLabResult':
      return { label: 'Open the unmatched results', to: '/lab/unmatched', search: { search: record.reference } };
  }
}

/** How many of a kind are still open: "—" for the kinds the IT officer acknowledges. */
export function openText(finding: SimulatorRecordFinding): string {
  if (finding.open == null) {
    return '—';
  }
  const open = finding.open.toLocaleString('en-IN');
  return finding.openAmount ? `${open}, ${formatInr(finding.openAmount)}` : open;
}

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

  if (latest.criticalOpen > 0) {
    return {
      intent: view.liveServer ? 'warning' : 'info',
      title: `${latest.criticalOpen.toLocaleString('en-IN')} ${latest.criticalOpen === 1 ? 'record' : 'records'} made by simulators not corrected yet`,
      text: `${swept} Correct each as its kind says (the records are listed under "To correct"), sweep again, and sign off that sweep. No sign-off is taken before.`,
    };
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
