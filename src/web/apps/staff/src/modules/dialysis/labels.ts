import type { StatusTone } from '@hc360/ui';
import type { Weekday } from '@/modules/ot/shared/types';
import type {
  DialysisComplication,
  DialysisMachineStatus,
  DialysisSessionStatus,
  DialysisShift,
  DialyzerReusePolicy,
  SeroStatus,
  VascularAccessType,
} from './api';

/** Words a dialysis nurse uses for the enums (UX rule 1). */

export const seroLabel: Record<SeroStatus, string> = {
  Negative: 'Sero-negative',
  HBsAg: 'HBsAg positive',
  HCV: 'HCV positive',
  HIV: 'HIV positive',
};

/** How the server describes a sero category inside sentences ("dedicated to HBsAg-positive patients"). */
export const seroInSentence: Record<SeroStatus, string> = {
  Negative: 'sero-negative',
  HBsAg: 'HBsAg-positive',
  HCV: 'HCV-positive',
  HIV: 'HIV-positive',
};

export const seroShort: Record<SeroStatus, string> = { Negative: 'Negative', HBsAg: 'HBsAg +', HCV: 'HCV +', HIV: 'HIV +' };

/** Distinct colours per segregation group; the label is always shown too (colour is never the only signal). */
export const seroTone: Record<SeroStatus, StatusTone> = { Negative: 'success', HBsAg: 'warning', HCV: 'severe', HIV: 'danger' };

export const accessLabel: Record<VascularAccessType, string> = { AVF: 'AV fistula (AVF)', AVG: 'AV graft (AVG)', Catheter: 'Dialysis catheter' };

export const shiftLabel: Record<DialysisShift, string> = { Morning: 'Morning', Afternoon: 'Afternoon', Evening: 'Evening', Night: 'Night' };

export const machineStatusLabel: Record<DialysisMachineStatus, string> = {
  Available: 'Available',
  InUse: 'In use',
  Maintenance: 'Under maintenance',
  Retired: 'Retired',
};

export const machineStatusTone: Record<DialysisMachineStatus, StatusTone> = { Available: 'success', InUse: 'brand', Maintenance: 'warning', Retired: 'subtle' };

export const sessionStatusLabel: Record<DialysisSessionStatus, string> = {
  Scheduled: 'Scheduled',
  InProgress: 'On dialysis',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
  Missed: 'Missed (did not come)',
};

export const sessionStatusTone: Record<DialysisSessionStatus, StatusTone> = {
  Scheduled: 'informative',
  InProgress: 'brand',
  Completed: 'success',
  Cancelled: 'subtle',
  Missed: 'danger',
};

export const complicationLabel: Record<DialysisComplication, string> = {
  Hypotension: 'Low blood pressure',
  MuscleCramps: 'Muscle cramps',
  NauseaVomiting: 'Nausea or vomiting',
  Headache: 'Headache',
  ChestPain: 'Chest pain',
  FeverRigors: 'Fever or rigors',
  Arrhythmia: 'Irregular heartbeat',
  AccessBleeding: 'Bleeding from the access',
  CircuitClotting: 'Circuit clotting',
  Hypoglycaemia: 'Low blood sugar',
  Other: 'Other',
};

export const reuseLabel: Record<DialyzerReusePolicy, string> = { SingleUse: 'Single use', Reuse: 'Reuse (reprocessed for this patient)' };

export const weekdayShort: Record<Weekday, string> = {
  Monday: 'Mon',
  Tuesday: 'Tue',
  Wednesday: 'Wed',
  Thursday: 'Thu',
  Friday: 'Fri',
  Saturday: 'Sat',
  Sunday: 'Sun',
};

/** Common disinfection methods offered as one-click choices. */
export const DISINFECTION_METHODS = [
  'Heat disinfection',
  'Heat with citric acid',
  'Chemical (peracetic acid)',
  'Chemical (sodium hypochlorite)',
  'Surface wipe-down and hot rinse',
] as const;
