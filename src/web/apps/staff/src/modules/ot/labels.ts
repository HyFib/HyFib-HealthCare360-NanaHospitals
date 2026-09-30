import type { StatusTone } from '@hc360/ui';
import type {
  AirwayDevice,
  AnaesthesiaType,
  AsaGrade,
  ConflictKind,
  Laterality,
  MaterialKind,
  SafetyPhase,
  SpecimenDestination,
  SurgeryPriority,
  SurgeryStatus,
  TheatreType,
} from './api';

/** Plain-language names for the OT enums (UX rule 1: words a nurse uses, never the enum). */

export const surgeryStatusLabel: Record<SurgeryStatus, string> = {
  Requested: 'Waiting for a slot',
  Scheduled: 'Scheduled',
  InPreOp: 'In pre-op',
  InProgress: 'In surgery',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
  Postponed: 'Postponed',
};

export const surgeryStatusTone: Record<SurgeryStatus, StatusTone> = {
  Requested: 'warning',
  Scheduled: 'informative',
  InPreOp: 'brand',
  InProgress: 'important',
  Completed: 'success',
  Cancelled: 'danger',
  Postponed: 'warning',
};

export const priorityLabel: Record<SurgeryPriority, string> = {
  Elective: 'Elective',
  Urgent: 'Urgent',
  Emergency: 'Emergency',
};

export const priorityTone: Record<SurgeryPriority, StatusTone> = {
  Elective: 'subtle',
  Urgent: 'warning',
  Emergency: 'danger',
};

export const lateralityLabel: Record<Laterality, string> = {
  NotApplicable: 'No side (midline or not applicable)',
  Left: 'Left',
  Right: 'Right',
  Bilateral: 'Both sides (bilateral)',
};

/** Short side label for boards and lists; null when there is no side. */
export function sideShort(laterality: Laterality): string | null {
  switch (laterality) {
    case 'Left':
      return 'LEFT';
    case 'Right':
      return 'RIGHT';
    case 'Bilateral':
      return 'BILATERAL';
    default:
      return null;
  }
}

export const anaesthesiaLabel: Record<AnaesthesiaType, string> = {
  GA: 'General anaesthesia (GA)',
  Spinal: 'Spinal',
  Epidural: 'Epidural',
  Regional: 'Regional block',
  Local: 'Local (given by the surgeon)',
  Sedation: 'Sedation',
};

export const anaesthesiaShort: Record<AnaesthesiaType, string> = {
  GA: 'GA',
  Spinal: 'Spinal',
  Epidural: 'Epidural',
  Regional: 'Block',
  Local: 'Local',
  Sedation: 'Sedation',
};

export const theatreTypeLabel: Record<TheatreType, string> = {
  Major: 'Major',
  Minor: 'Minor',
  Cardiac: 'Cardiac',
  Ortho: 'Orthopaedic',
  Emergency: 'Emergency',
};

export const phaseLabel: Record<SafetyPhase, string> = {
  SignIn: 'WHO Sign-in',
  TimeOut: 'WHO Time-out',
  SignOut: 'WHO Sign-out',
};

export const phaseMoment: Record<SafetyPhase, string> = {
  SignIn: 'Before induction of anaesthesia',
  TimeOut: 'Before skin incision',
  SignOut: 'Before the patient leaves the theatre',
};

export const asaLabel: Record<AsaGrade, string> = {
  I: 'ASA I — healthy patient',
  II: 'ASA II — mild systemic disease',
  III: 'ASA III — severe systemic disease',
  IV: 'ASA IV — severe disease, constant threat to life',
  V: 'ASA V — not expected to survive without the operation',
  VI: 'ASA VI — brain-dead organ donor',
};

export const airwayLabel: Record<AirwayDevice, string> = {
  None: 'None (spontaneous breathing)',
  FaceMask: 'Face mask',
  SupraglotticAirway: 'Supraglottic airway (LMA / i-gel)',
  EndotrachealTube: 'Endotracheal tube',
  Tracheostomy: 'Tracheostomy',
};

export const specimenLabel: Record<SpecimenDestination, string> = {
  Histopathology: 'Histopathology',
  Microbiology: 'Microbiology',
  Cytology: 'Cytology',
  FrozenSection: 'Frozen section',
  Other: 'Other',
};

export const materialKindLabel: Record<MaterialKind, string> = { Implant: 'Implant', Consumable: 'Consumable' };

export const conflictLabel: Record<ConflictKind, string> = {
  Theatre: 'The theatre is already booked',
  Surgeon: 'The surgeon is already operating',
  Anaesthetist: 'The anaesthetist is already allocated',
  Patient: 'The patient already has a surgery',
};
