import type { VisitListItem, VisitType } from '@/modules/patients/api';

/** How far back the visit a stay comes from may be: as far as an admission may be dated back (three days). */
export const SOURCE_VISIT_DAYS = 3;

const VISIT_TYPE_OF_SOURCE: Partial<Record<string, VisitType>> = { OutPatient: 'OutPatient', Emergency: 'Emergency' };

/**
 * The visits a stay may come from, latest first: for "Advised in OPD" the patient's out-patient visits, for "From
 * casualty / ER" her casualty visits, of the last three days and not cancelled. The admission links the one chosen
 * (the first by default), so the stay goes on from it: its free treatment of a victim (BNSS 2023 s.397) and its record.
 */
export function sourceVisitChoices<T extends Pick<VisitListItem, 'id' | 'type' | 'status' | 'visitedAt'>>(
  visits: readonly T[],
  source: string,
  now: number,
): T[] {
  const type = VISIT_TYPE_OF_SOURCE[source];
  if (!type) {
    return [];
  }
  const since = now - SOURCE_VISIT_DAYS * 24 * 60 * 60 * 1000;
  return visits
    .filter((v) => v.type === type && v.status !== 'Cancelled' && Date.parse(v.visitedAt) >= since)
    .toSorted((a, b) => Date.parse(b.visitedAt) - Date.parse(a.visitedAt));
}
