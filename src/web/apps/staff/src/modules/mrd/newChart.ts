import { formatDateTime } from '@hc360/core';
import { encounterTypeLabel, type EncounterListItem } from '@/modules/emr/api';

/**
 * The encounters a chart can be opened for by hand: an in-patient stay's chart is opened at admission and follows the
 * admission, so only out-patient, emergency, teleconsultation and check-up encounters are offered.
 */
export function chartableEncounters(encounters: EncounterListItem[]): EncounterListItem[] {
  return encounters.filter((encounter) => encounter.type !== 'InPatient');
}

export function encounterOptionLabel(encounter: EncounterListItem): string {
  return [
    encounterTypeLabel[encounter.type],
    formatDateTime(encounter.startedAt),
    encounter.practitionerName,
    encounter.departmentName,
    encounter.status === 'InProgress' ? 'not signed' : null,
  ]
    .filter(Boolean)
    .join(' · ');
}
