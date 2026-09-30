/**
 * Prescribing-safety presentation rules (pure, unit-tested). The server decides what is blocking; these helpers
 * group the alerts for display and work out exactly what the doctor must do before the Sign button unlocks:
 * a clinical override reason for blocking alerts, an acknowledgement for warnings and an explicit confirmation for
 * Schedule H1/X medicines.
 */

export type SafetyAlertKind =
  | 'Allergy'
  | 'ClassAllergy'
  | 'CrossSensitivity'
  | 'DuplicateTherapy'
  | 'Interaction'
  | 'MaxDose'
  | 'DoseUnverified'
  | 'HighAlertMedication'
  | 'ControlledDrug';

export type SafetyAlertSeverity = 'Info' | 'Minor' | 'Moderate' | 'Major';

export interface SafetyAlert {
  key: string;
  kind: SafetyAlertKind;
  severity: SafetyAlertSeverity;
  isBlocking: boolean;
  message: string;
  detail: string | null;
  itemIds: string[];
}

export interface SafetyCheckResponse {
  isBlocked: boolean;
  requiresAcknowledgement: boolean;
  hasControlledDrugs: boolean;
  alerts: SafetyAlert[];
}

/** Minimum length of the audited clinical override reason (server rule). */
export const MIN_OVERRIDE_REASON = 10;

export const alertKindLabel: Record<SafetyAlertKind, string> = {
  Allergy: 'Allergy',
  ClassAllergy: 'Drug-class allergy',
  CrossSensitivity: 'Cross-sensitivity',
  DuplicateTherapy: 'Duplicate therapy',
  Interaction: 'Drug interaction',
  MaxDose: 'Above the maximum daily dose',
  DoseUnverified: 'Dose could not be checked',
  HighAlertMedication: 'High-alert medicine',
  ControlledDrug: 'Schedule H1/X medicine',
};

const SEVERITY_WEIGHT: Record<SafetyAlertSeverity, number> = { Major: 3, Moderate: 2, Minor: 1, Info: 0 };

export interface GroupedAlerts {
  /** Stop signing unless the doctor gives a clinical override reason. */
  blocking: SafetyAlert[];
  /** Must be acknowledged before signing. */
  warnings: SafetyAlert[];
  /** Schedule H1/X medicines: explicit confirmation (and a register entry) when signing. */
  controlled: SafetyAlert[];
}

function bySeverity(a: SafetyAlert, b: SafetyAlert): number {
  return SEVERITY_WEIGHT[b.severity] - SEVERITY_WEIGHT[a.severity] || a.message.localeCompare(b.message);
}

/** Splits alerts the way the server enforces them (de-duplicated by key, most severe first). */
export function groupAlerts(alerts: readonly SafetyAlert[]): GroupedAlerts {
  const unique = [...new Map(alerts.map((alert) => [alert.key, alert])).values()];
  return {
    blocking: unique.filter((a) => a.isBlocking).toSorted(bySeverity),
    warnings: unique.filter((a) => !a.isBlocking && a.kind !== 'ControlledDrug').toSorted(bySeverity),
    controlled: unique.filter((a) => !a.isBlocking && a.kind === 'ControlledDrug').toSorted(bySeverity),
  };
}

export interface SignRequirements {
  needsOverride: boolean;
  needsAcknowledgement: boolean;
  needsControlledConfirmation: boolean;
}

export function signRequirements(grouped: GroupedAlerts, hasControlledDrugs: boolean): SignRequirements {
  return {
    needsOverride: grouped.blocking.length > 0,
    needsAcknowledgement: grouped.warnings.length > 0,
    needsControlledConfirmation: hasControlledDrugs || grouped.controlled.length > 0,
  };
}

export interface SignDecision {
  overrideReason: string;
  acknowledgeWarnings: boolean;
  confirmControlledDrugs: boolean;
}

export const emptyDecision: SignDecision = {
  overrideReason: '',
  acknowledgeWarnings: false,
  confirmControlledDrugs: false,
};

/** What still stops signing, in plain words (empty = ready to sign). */
export function signBlockers(requirements: SignRequirements, decision: SignDecision): string[] {
  const blockers: string[] = [];
  const reason = decision.overrideReason.trim();
  if (requirements.needsOverride && reason.length < MIN_OVERRIDE_REASON) {
    blockers.push(
      reason.length === 0
        ? 'Give the clinical reason for overriding the blocking alerts, or change the prescription.'
        : `The override reason needs at least ${MIN_OVERRIDE_REASON} characters (${reason.length} so far).`,
    );
  }
  if (requirements.needsAcknowledgement && !decision.acknowledgeWarnings) {
    blockers.push('Tick that you have reviewed the warnings.');
  }
  if (requirements.needsControlledConfirmation && !decision.confirmControlledDrugs) {
    blockers.push('Confirm the Schedule H1/X medicines.');
  }
  return blockers;
}

export interface SignPrescriptionRequest {
  overrideReason: string | null;
  acknowledgeWarnings: boolean;
  confirmControlledDrugs: boolean;
  version: number;
}

/** The sign request: the override reason is sent only when something is actually blocking (it is audited). */
export function toSignRequest(
  requirements: SignRequirements,
  decision: SignDecision,
  version: number,
): SignPrescriptionRequest {
  return {
    overrideReason: requirements.needsOverride ? decision.overrideReason.trim() : null,
    acknowledgeWarnings: requirements.needsAcknowledgement ? decision.acknowledgeWarnings : false,
    confirmControlledDrugs: requirements.needsControlledConfirmation ? decision.confirmControlledDrugs : false,
    version,
  };
}

/** Error codes the sign/refill endpoints use when the doctor still has to act on safety alerts. */
export const SAFETY_ERROR_CODES = [
  'prescription.safety_block',
  'prescription.warnings_unacknowledged',
  'prescription.controlled_confirmation_required',
] as const;

function isSafetyAlert(value: unknown): value is SafetyAlert {
  return (
    typeof value === 'object' &&
    value !== null &&
    'key' in value &&
    'kind' in value &&
    'message' in value &&
    'isBlocking' in value
  );
}

/** Alerts carried in a 422 problem (`data`), or null when the error is not a safety stop. */
export function alertsFromProblem(code: string | undefined, data: unknown): SafetyAlert[] | null {
  if (!code || !(SAFETY_ERROR_CODES as readonly string[]).includes(code)) {
    return null;
  }
  return Array.isArray(data)
    ? data.filter(isSafetyAlert).map((a) => ({ ...a, itemIds: Array.isArray(a.itemIds) ? a.itemIds : [] }))
    : [];
}

/** Names of the prescription lines an alert refers to (current medicines are named inside the message). */
export function affectedDrugs(alert: SafetyAlert, items: readonly { id: string; drugName: string }[]): string[] {
  const names = alert.itemIds
    .map((id) => items.find((item) => item.id === id)?.drugName)
    .filter((name): name is string => Boolean(name));
  return [...new Set(names)];
}

/** Badge colour for an alert severity. */
export function severityTone(severity: SafetyAlertSeverity): 'danger' | 'warning' | 'informative' {
  return severity === 'Major' ? 'danger' : severity === 'Moderate' || severity === 'Minor' ? 'warning' : 'informative';
}
