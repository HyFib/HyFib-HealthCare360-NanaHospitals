import { formatDateTime, isApiError } from '@hc360/core';
import type { AllergyStatus as BannerAllergyStatus } from '@hc360/ui';
import type { AllergyRecordingStatus, AllergyStatusDto } from './api';

/**
 * The chart's allergy status as the screens show it (pure, unit-tested). "No known allergies" is claimed only when a
 * clinician recorded it (server `AllergyRecordingStatus`), never inferred from an empty allergy list (EHR-03).
 */

const BANNER_STATUS: Record<AllergyRecordingStatus, BannerAllergyStatus> = {
  Recorded: 'recorded',
  NoKnownAllergies: 'none',
  NotRecorded: 'unknown',
};

/** The patient banner's allergy flag; an unknown value reads as "not recorded", the safe default. */
export function bannerAllergyStatus(status: AllergyRecordingStatus): BannerAllergyStatus {
  return BANNER_STATUS[status] ?? 'unknown';
}

const ALLERGY_NOTE: Record<AllergyRecordingStatus, string | null> = {
  Recorded: null,
  NoKnownAllergies: 'No known allergies.',
  NotRecorded: 'Allergies have not been recorded yet.',
};

/**
 * The line under "Alerts and allergies" on the chart summary when no active allergy is listed: "No active alerts."
 * (when there are none) and what is known about allergies. Null while allergies are listed.
 */
export function flagsSummaryNote(status: AllergyRecordingStatus, alertCount: number, allergyCount: number): string | null {
  if (allergyCount > 0) {
    return null;
  }
  const parts = [alertCount === 0 ? 'No active alerts.' : null, ALLERGY_NOTE[status]].filter((part): part is string => Boolean(part));
  return parts.length > 0 ? parts.join(' ') : null;
}

/**
 * "No known allergies" is offered to allergy managers only while the chart has neither active allergies nor the
 * statement (`activeAllergies` is what the screen lists, so a stale status never offers it next to an allergy).
 */
export function canRecordNoKnownAllergies(status: AllergyRecordingStatus | undefined, canManage: boolean, activeAllergies: number): boolean {
  return canManage && status === 'NotRecorded' && activeAllergies === 0;
}

/** "Recorded by Dr. Asha Rao on 28 Sept 2026, 10:30 am." — who confirmed the status and when. */
export function recordedByText(status: Pick<AllergyStatusDto, 'recordedAt' | 'recordedByName'>, formatWhen: (iso: string) => string = formatDateTime): string {
  const who = status.recordedByName?.trim() || 'a clinician';
  return status.recordedAt ? `Recorded by ${who} on ${formatWhen(status.recordedAt)}.` : `Recorded by ${who}.`;
}

function activeAllergyCount(data: unknown): number | null {
  const count = typeof data === 'object' && data !== null ? (data as { activeAllergies?: unknown }).activeAllergies : undefined;
  return typeof count === 'number' && count > 0 ? count : null;
}

/**
 * Why "No known allergies" was refused (409 `allergy.active_exist`: active allergies are on the chart), in plain
 * words with what to do; null for any other error.
 */
export function noKnownAllergiesRefusal(error: unknown): string | null {
  if (!isApiError(error) || error.code !== 'allergy.active_exist') {
    return null;
  }
  const count = activeAllergyCount(error.data);
  const one = count === 1;
  const what = count === null ? 'active allergies' : one ? 'an active allergy' : `${count} active allergies`;
  return (
    `This patient has ${what} on the chart, so "No known allergies" was not recorded. ` +
    `If ${one ? 'it was' : 'they were'} recorded by mistake or no longer ${one ? 'applies' : 'apply'}, ` +
    `open ${one ? 'it' : 'them'} and mark ${one ? 'it' : 'them'} entered in error, inactive or resolved first.`
  );
}
