import type { SafetyAlert } from '@/modules/emr/safety';

/**
 * The prescribing safety check of a ward order (pure, unit-tested). The server checks a ward order as it checks an
 * e-prescription line of the same medicine at signing; the order form shows what it found with the prescription's own
 * safety review, and a blocking alert needs the clinical reason for ordering it anyway.
 */

/** One finding, as the server sends it for a ward order: an e-prescription's alert, naming no line (the order is one). */
export type WardSafetyAlert = Omit<SafetyAlert, 'itemIds'>;

/** What the check found (`POST /api/v1/nursing/admissions/{id}/medication-orders/check`), blocking alerts first. */
export interface WardOrderSafety {
  isBlocked: boolean;
  alerts: WardSafetyAlert[];
}

/** The refusal of a ward order a blocking alert stops; its problem carries every alert (`data`). */
export const WARD_SAFETY_BLOCK = 'medication.safety_block';

/** A ward order's alerts as the prescription's safety review takes them. */
export function asSafetyAlerts(alerts: readonly WardSafetyAlert[]): SafetyAlert[] {
  return alerts.map((alert) => ({ ...alert, itemIds: [] }));
}

function isWardAlert(value: unknown): value is WardSafetyAlert {
  return (
    typeof value === 'object' &&
    value !== null &&
    'key' in value &&
    'kind' in value &&
    'message' in value &&
    'isBlocking' in value
  );
}

/** The alerts a refused ward order carries, or null when it was refused for something else. */
export function wardAlertsFromProblem(code: string | undefined, data: unknown): SafetyAlert[] | null {
  if (code !== WARD_SAFETY_BLOCK) {
    return null;
  }
  return Array.isArray(data) ? asSafetyAlerts(data.filter(isWardAlert)) : [];
}

/** What the check reads of the order: when any of it changes, the order is checked again before it is written. */
export interface CheckedMedicine {
  drugItemId?: string | null;
  drugName?: string;
  genericName?: string;
  strength?: string;
  dose?: string;
  route?: string;
  frequency?: string;
}

export function medicineKey(m: CheckedMedicine): string {
  return JSON.stringify([
    m.drugItemId ?? null,
    ...[m.drugName, m.genericName, m.strength, m.dose, m.route, m.frequency].map((v) => (v ?? '').trim()),
  ]);
}
