import type { MedicationOrderDto } from './nursingApi';

/**
 * Whether a ward order written now is the doctor's verbal or telephone order rather than one the writer signs: only a
 * prescriber (`clinical.prescriptions.sign`) writing in their own name (their own practitioner record, or none named)
 * signs at once. Anyone else, a doctor writing another doctor's order included, says how the doctor gave it, reads it
 * back, and the doctor countersigns it. The server decides the same way and refuses otherwise.
 */
export function writesVerbalOrder(
  orderedBy: string | null,
  myPractitionerId: string | null,
  canSign: boolean,
): boolean {
  return !(canSign && myPractitionerId !== null && (orderedBy === null || orderedBy === myPractitionerId));
}

/** What waits on an order for the doctor, in words for the chart; null when nothing waits. */
export function countersignState(order: MedicationOrderDto): { label: string; overdue: boolean } | null {
  if (order.stopPending) {
    return {
      label: order.stopCountersignOverdue ? 'Stop pending: overdue' : 'Stop pending: doses held',
      overdue: order.stopCountersignOverdue,
    };
  }
  if (order.awaitingCountersignature) {
    return {
      label: order.countersignOverdue ? 'Countersignature overdue' : 'Awaiting countersignature',
      overdue: order.countersignOverdue,
    };
  }
  return null;
}
