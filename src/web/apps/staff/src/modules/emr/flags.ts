import { isApiError } from '@hc360/core';
import type { PatientBannerAlert, PatientBannerAlertTone, PatientBannerData, PatientFlagsData } from '@hc360/ui';
import { bannerAllergyStatus } from './allergyStatus';
import type { AlertSeverity, AlertSummary, AllergySummary, PatientFlags } from './api';

/**
 * The patient banner's safety flags as the screens show them (pure, unit-tested): allergies, "no known allergies" or
 * "not recorded", and the current alerts toned by severity (EHR-03). Shared by the chart's banner and every other
 * clinical screen's, so all of them say the same thing.
 */

/** Who may see the flags: the flags permission, or the whole chart. */
export const FLAG_READERS = ['clinical.flags.read', 'clinical.emr.read'] as const;

/** "Penicillin (anaphylaxis)": severity is shown for the reactions that matter most. */
export function allergyText(allergy: Pick<AllergySummary, 'allergen' | 'severity'>): string {
  return allergy.severity === 'Severe' || allergy.severity === 'Anaphylaxis'
    ? `${allergy.allergen} (${allergy.severity.toLowerCase()})`
    : allergy.allergen;
}

const ALERT_TONE: Record<AlertSeverity, PatientBannerAlertTone> = {
  Critical: 'danger',
  Warning: 'warning',
  Info: 'informative',
};

/** "Infection control: MRSA", in red when critical (DNR, isolation, infection control by default). */
export function alertBadge(alert: Pick<AlertSummary, 'title' | 'detail' | 'severity'>): PatientBannerAlert {
  return {
    label: alert.detail ? `${alert.title}: ${alert.detail}` : alert.title,
    // An unknown severity from a newer server is shown as a warning rather than played down.
    tone: ALERT_TONE[alert.severity] ?? 'warning',
  };
}

/** The banner fields that the flags fill in. */
export type FlagFields = Required<Pick<PatientBannerData, 'allergies' | 'allergyStatus' | 'alerts'>>;

export function flagFields(flags: PatientFlags): FlagFields {
  return {
    allergies: flags.allergies.map(allergyText),
    // "No known allergies" only when a clinician recorded it; an empty list alone shows "Allergies not recorded".
    allergyStatus: bannerAllergyStatus(flags.allergyStatus),
    alerts: flags.alerts.map(alertBadge),
  };
}

/** Where a screen's banner flags stand. */
export type FlagsState =
  /** The user may not see clinical flags (front office, billing): the banner shows identity only. */
  | { kind: 'hidden' }
  | { kind: 'loading' }
  | { kind: 'ready'; flags: PatientFlags }
  | { kind: 'failed'; error: unknown };

/** What a screen shows for a patient with no record yet (an unregistered emergency patient): nothing recorded. */
export const NO_RECORD: PatientFlags = { allergyStatus: 'NotRecorded', allergies: [], alerts: [] };

/** Why the flags are missing, in words that tell the reader not to assume there are none. */
export function unavailableNote(error: unknown): string {
  return isApiError(error) && error.code === 'patient.restricted'
    ? 'Allergies and alerts hidden: restricted record'
    : 'Allergies and alerts could not be loaded';
}

function labelOf(alert: string | PatientBannerAlert): string {
  return (typeof alert === 'string' ? alert : alert.label).trim().toLowerCase();
}

/** The clinical alerts first, then the screen's own (bed alerts, medico-legal case…), without repeating a label. */
export function mergeAlerts(
  clinical: readonly (string | PatientBannerAlert)[],
  screen: readonly (string | PatientBannerAlert)[],
): (string | PatientBannerAlert)[] {
  const seen = new Set<string>();
  return [...clinical, ...screen].filter((alert) => {
    const label = labelOf(alert);
    if (seen.has(label)) {
      return false;
    }
    seen.add(label);
    return true;
  });
}

/**
 * What the flags show in each state; null when the user may not see clinical flags. While they load it says "Checking
 * allergies…", and when they cannot be had it says so, so a missing allergy flag is never read as "no allergies".
 */
export function flagsView(state: FlagsState): PatientFlagsData | null {
  switch (state.kind) {
    case 'hidden':
      return null;
    case 'loading':
      return { allergyStatus: 'checking' };
    case 'failed':
      return { allergyStatus: 'unavailable', allergyNote: unavailableNote(state.error) };
    case 'ready':
      return flagFields(state.flags);
  }
}

/** The screen's banner with the clinical flags merged in, the clinical alerts ahead of the screen's own. */
export function withFlags(patient: PatientBannerData, state: FlagsState): PatientBannerData {
  const view = flagsView(state);
  return view ? { ...patient, ...view, alerts: mergeAlerts(view.alerts ?? [], patient.alerts ?? []) } : patient;
}
