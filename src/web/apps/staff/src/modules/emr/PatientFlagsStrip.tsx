import { PatientFlagBadges } from '@hc360/ui';
import { flagsView } from './flags';
import { usePatientFlags } from './hooks';

/**
 * A patient's allergies and alerts without the rest of the banner (EHR-03), for dialogs and counters that act on a
 * patient the screen does not otherwise introduce: sample collection, the pharmacy counter, diet orders. Kept live like
 * the banner; renders nothing for staff who may not see clinical flags.
 */
export function PatientFlagsStrip({ patientId }: { patientId: string | null | undefined }) {
  const view = flagsView(usePatientFlags(patientId));
  return view ? <PatientFlagBadges flags={view} /> : null;
}
