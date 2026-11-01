import type { PagedResult } from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { VisitListItem, VisitType } from '@/modules/patients/api';

/** How far back the visit a stay comes from may be: as far as an admission may be dated back (three days). */
export const SOURCE_VISIT_DAYS = 3;

const DAY = 24 * 60 * 60 * 1000;

const VISIT_TYPE_OF_SOURCE: Partial<Record<string, VisitType>> = { OutPatient: 'OutPatient', Emergency: 'Emergency' };

/**
 * The visits a stay may come from, latest first: for "Advised in OPD" the patient's out-patient visits, for "From
 * casualty / ER" her casualty visits, not cancelled, of the three days before the admission and none after it. An
 * admission entered as earlier counts back from its `admittedAt`; one made now, from `now`. The admission links the
 * one chosen (the first by default), so the stay goes on from it: its free treatment of a victim (BNSS 2023 s.397) and
 * its record. The server takes no other visit (Inpatient `AdmissionRules.SourceVisitRefusal`): keep the two in step.
 */
export function sourceVisitChoices<T extends Pick<VisitListItem, 'id' | 'type' | 'status' | 'visitedAt'>>(
  visits: readonly T[],
  source: string,
  admittedAt: number | null,
  now: number,
): T[] {
  const type = VISIT_TYPE_OF_SOURCE[source];
  if (!type) {
    return [];
  }
  const until = admittedAt ?? Number.POSITIVE_INFINITY;
  const since = (admittedAt ?? now) - SOURCE_VISIT_DAYS * DAY;
  return visits
    .filter((v) => {
      const at = Date.parse(v.visitedAt);
      return v.type === type && v.status !== 'Cancelled' && at >= since && at <= until;
    })
    .toSorted((a, b) => Date.parse(b.visitedAt) - Date.parse(a.visitedAt));
}

/**
 * The patient's latest visits, as many as the server gives at once (200, latest first), so every visit a stay may come
 * from is among them however many other visits she had: none is more than six days old (three days before an
 * admission dated back three days). Kept under the patient's visits, so whatever refreshes those refreshes this.
 */
export const sourceVisitsQuery = (patientId: string) =>
  queryOptions({
    queryKey: ['patients', 'visits', patientId, 'admission-source'] as const,
    queryFn: ({ signal }) =>
      api.get<PagedResult<VisitListItem>>(`/api/v1/patients/${patientId}/visits`, {
        query: { page: 1, pageSize: 200 },
        signal,
      }),
  });
