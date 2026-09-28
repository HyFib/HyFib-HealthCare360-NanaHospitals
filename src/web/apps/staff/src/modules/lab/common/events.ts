/**
 * Maps the realtime events the diagnostics module pushes on `lab:{facilityId}` to the query-key prefixes that must be
 * refetched. Payloads carry ids only; screens refetch with their own permissions (and PHI audit) instead of trusting
 * pushed data. Keeping the map narrow avoids refetch storms when an analyser streams results.
 */
export type KeyPrefix = readonly string[];

const ORDERS: KeyPrefix = ['lab', 'orders'];
const SAMPLES: KeyPrefix = ['lab', 'samples'];
const WORKLIST: KeyPrefix = ['lab', 'worklist'];
const SHEET: KeyPrefix = ['lab', 'sheet'];
const REPORTS: KeyPrefix = ['lab', 'reports'];
const TAT: KeyPrefix = ['lab', 'tat'];
const CRITICAL: KeyPrefix = ['lab', 'critical-calls'];
const OUTSOURCING: KeyPrefix = ['lab', 'outsourcing'];
const QC: KeyPrefix = ['lab', 'qc'];

const LAB_EVENTS: Record<string, KeyPrefix[]> = {
  'lab.order.created': [ORDERS, SAMPLES, REPORTS],
  'lab.order.cancelled': [ORDERS, SAMPLES, WORKLIST, SHEET, REPORTS, OUTSOURCING],
  'lab.sample.collected': [ORDERS, SAMPLES, OUTSOURCING],
  'lab.sample.received': [ORDERS, SAMPLES, WORKLIST],
  'lab.sample.rejected': [ORDERS, SAMPLES, WORKLIST, SHEET, TAT],
  'lab.sample.recollection': [ORDERS, SAMPLES, WORKLIST],
  'lab.results.entered': [ORDERS, WORKLIST, SHEET],
  'lab.results.received': [ORDERS, WORKLIST, SHEET],
  'lab.results.verified': [ORDERS, WORKLIST, SHEET, REPORTS, TAT],
  'lab.results.reopened': [ORDERS, WORKLIST, SHEET, REPORTS],
  'lab.critical': [WORKLIST, SHEET, CRITICAL],
  'lab.report.released': [ORDERS, REPORTS, TAT],
  'lab.report.amended': [ORDERS, REPORTS, SHEET],
  'lab.qc.run': [QC],
  'lab.qc.override': [QC],
  'lab.outsourcing.sent': [OUTSOURCING, ORDERS],
  'lab.outsourcing.received': [OUTSOURCING, ORDERS, WORKLIST],
};

/** Query-key prefixes to invalidate for a pushed event (unknown lab events refresh the lab work lists). */
export function invalidationTargets(eventName: string): KeyPrefix[] {
  const mapped = LAB_EVENTS[eventName];
  if (mapped) {
    return mapped;
  }
  if (eventName.startsWith('lab.')) {
    return [ORDERS, SAMPLES, WORKLIST, SHEET, REPORTS];
  }
  if (eventName.startsWith('radiology.')) {
    return [['radiology']];
  }
  if (eventName.startsWith('cardiology.')) {
    return [['cardiology']];
  }
  return [];
}

/** Merges prefixes, dropping duplicates and prefixes already covered by a shorter one. */
export function mergePrefixes(prefixes: readonly KeyPrefix[]): KeyPrefix[] {
  const sorted = [...prefixes].sort((a, b) => a.length - b.length);
  const result: KeyPrefix[] = [];
  for (const prefix of sorted) {
    const covered = result.some((kept) => kept.length <= prefix.length && kept.every((part, index) => prefix[index] === part));
    if (!covered) {
      result.push(prefix);
    }
  }
  return result;
}
