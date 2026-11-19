import type { MedicationOrderDto } from '@/modules/inpatient/nursingApi';

/**
 * Which in-patient e-prescription a ward indent supplies (pure, unit-tested). A medicine on the indent that an active
 * e-prescription line of the patient's chart names (the same drug of the master) is supplied for that prescription: the
 * indent carries the prescription, so the IP pharmacy links the issue to its line, the chart charts its own line, and the
 * prescription's queue does not issue and charge it again. An indent carries one prescription: a medicine of another is
 * indented on its own.
 */

/** The chart line a medicine on the indent is e-prescribed on. */
export interface IndentLineOrder {
  prescriptionId: string;
  drugName: string;
  dose: string;
  frequency: string;
}

export interface IndentPrescription {
  /** The prescription the indent is raised for: that of its first e-prescribed medicine; null when none is. */
  prescriptionId: string | null;
  /** Each e-prescribed medicine on the indent, by item: its chart line, and whether it is the indent's prescription's. */
  lines: Record<string, { order: IndentLineOrder; forIndent: boolean }>;
}

export function indentPrescription(
  itemIds: readonly string[],
  orders: readonly MedicationOrderDto[],
): IndentPrescription {
  const prescribed = orders.filter(
    (o) => o.status === 'Active' && o.source === 'Prescription' && o.prescriptionId !== null && o.drugItemId !== null,
  );
  let prescriptionId: string | null = null;
  const lines: IndentPrescription['lines'] = {};
  for (const itemId of itemIds) {
    const candidates = prescribed.filter((o) => o.drugItemId === itemId);
    const order = candidates.find((o) => o.prescriptionId === prescriptionId) ?? candidates[0];
    if (!order?.prescriptionId) {
      continue;
    }
    prescriptionId ??= order.prescriptionId;
    lines[itemId] = {
      order: {
        prescriptionId: order.prescriptionId,
        drugName: order.drugName,
        dose: order.dose,
        frequency: order.frequency,
      },
      forIndent: order.prescriptionId === prescriptionId,
    };
  }
  return { prescriptionId, lines };
}
