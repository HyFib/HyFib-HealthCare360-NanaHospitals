import { bloodGroupLabel, type BloodGroup } from '@hc360/core';
import type { StatusTone } from '@hc360/ui';
import type {
  BloodBagType,
  BloodComponentType,
  BloodRequestStatus,
  BloodRequestUrgency,
  BloodUnitStatus,
  CrossmatchMethod,
  DonationStatus,
  DonationType,
  DonorEligibilityOutcome,
  ReactionSeverity,
  ReactionStatus,
  ReactionSymptom,
  ReactionType,
  TtiPanel,
  TtiResult,
} from './api';

/** "A+" etc.; "Not grouped" instead of "Unknown" so nobody reads it as a group. */
export function groupLabel(group: BloodGroup | null | undefined): string {
  return !group || group === 'Unknown' ? 'Not grouped' : bloodGroupLabel[group];
}

export const componentLabel: Record<BloodComponentType, string> = {
  WholeBlood: 'Whole blood',
  PRBC: 'Packed red cells (PRBC)',
  FFP: 'Fresh frozen plasma (FFP)',
  PlateletConcentrate: 'Platelet concentrate',
  Cryoprecipitate: 'Cryoprecipitate',
};

/** Short names for grids and labels. */
export const componentShort: Record<BloodComponentType, string> = {
  WholeBlood: 'WB',
  PRBC: 'PRBC',
  FFP: 'FFP',
  PlateletConcentrate: 'PLT',
  Cryoprecipitate: 'CRYO',
};

/** Storage condition printed on unit labels. */
export const storageCondition: Record<BloodComponentType, string> = {
  WholeBlood: 'Store at 2–6 °C',
  PRBC: 'Store at 2–6 °C',
  FFP: 'Store at −30 °C or colder',
  PlateletConcentrate: 'Store at 20–24 °C, agitated',
  Cryoprecipitate: 'Store at −30 °C or colder',
};

export const unitStatusLabel: Record<BloodUnitStatus, string> = {
  Quarantine: 'Quarantine — not for issue',
  Available: 'Available',
  Reserved: 'Reserved (cross-matched)',
  Issued: 'Issued',
  Discarded: 'Discarded',
  Expired: 'Expired',
};

export const unitStatusTone: Record<BloodUnitStatus, StatusTone> = {
  Quarantine: 'warning',
  Available: 'success',
  Reserved: 'brand',
  Issued: 'informative',
  Discarded: 'danger',
  Expired: 'danger',
};

export const ttiResultLabel: Record<TtiResult, string> = {
  Pending: 'Pending',
  NonReactive: 'Non-reactive',
  Reactive: 'REACTIVE',
};

export const ttiMarkers: readonly { key: keyof TtiPanel; label: string }[] = [
  { key: 'hiv', label: 'HIV 1 & 2' },
  { key: 'hbsAg', label: 'Hepatitis B (HBsAg)' },
  { key: 'hcv', label: 'Hepatitis C (HCV)' },
  { key: 'syphilis', label: 'Syphilis' },
  { key: 'malaria', label: 'Malaria' },
];

export const donationTypeLabel: Record<DonationType, string> = {
  Voluntary: 'Voluntary',
  Replacement: 'Replacement (for a patient)',
};

export const bagTypeLabel: Record<BloodBagType, string> = {
  Single: 'Single',
  Double: 'Double',
  Triple: 'Triple',
  Quadruple: 'Quadruple',
};

export const donationStatusLabel: Record<DonationStatus, string> = {
  Collected: 'Collected — to separate',
  Separated: 'Separated',
  Discarded: 'Discarded',
};

export const outcomeLabel: Record<DonorEligibilityOutcome, string> = {
  Eligible: 'Eligible to donate',
  TemporarilyDeferred: 'Deferred for now',
  PermanentlyDeferred: 'Permanently deferred',
};

export const outcomeTone: Record<DonorEligibilityOutcome, StatusTone> = {
  Eligible: 'success',
  TemporarilyDeferred: 'warning',
  PermanentlyDeferred: 'danger',
};

export const urgencyLabel: Record<BloodRequestUrgency, string> = {
  Routine: 'Routine',
  Urgent: 'Urgent',
  Emergency: 'Emergency',
};

export const urgencyTone: Record<BloodRequestUrgency, StatusTone> = {
  Routine: 'subtle',
  Urgent: 'warning',
  Emergency: 'danger',
};

export const requestStatusLabel: Record<BloodRequestStatus, string> = {
  Pending: 'Waiting for sample',
  InProcess: 'Grouped — cross-matching',
  Ready: 'Ready to issue',
  PartiallyIssued: 'Partly issued',
  Issued: 'Issued',
  Cancelled: 'Cancelled',
};

export const requestStatusTone: Record<BloodRequestStatus, StatusTone> = {
  Pending: 'warning',
  InProcess: 'informative',
  Ready: 'success',
  PartiallyIssued: 'brand',
  Issued: 'subtle',
  Cancelled: 'subtle',
};

export const crossmatchMethodLabel: Record<CrossmatchMethod, string> = {
  ImmediateSpin: 'Immediate spin',
  AntiHumanGlobulin: 'Anti-human globulin (AHG / Coombs)',
  GelCard: 'Gel card',
  Electronic: 'Electronic',
};

export const symptomLabel: Record<ReactionSymptom, string> = {
  Fever: 'Fever',
  ChillsRigors: 'Chills or rigors',
  Urticaria: 'Urticaria (hives)',
  Itching: 'Itching',
  Flushing: 'Flushing',
  Dyspnoea: 'Breathlessness',
  Wheeze: 'Wheeze',
  Hypotension: 'Low blood pressure',
  Hypertension: 'High blood pressure',
  Tachycardia: 'Fast heart rate',
  ChestPain: 'Chest pain',
  BackPain: 'Back or loin pain',
  Haemoglobinuria: 'Red or dark urine',
  Oliguria: 'Little or no urine',
  NauseaVomiting: 'Nausea or vomiting',
  Other: 'Other',
};

/** Haemovigilance Programme of India classification. */
export const reactionTypeLabel: Record<ReactionType, string> = {
  Unclassified: 'Not classified yet',
  FebrileNonHaemolytic: 'Febrile non-haemolytic reaction',
  AllergicMinor: 'Allergic (minor)',
  Anaphylactic: 'Anaphylactic / anaphylactoid',
  AcuteHaemolytic: 'Acute haemolytic reaction',
  DelayedHaemolytic: 'Delayed haemolytic reaction',
  TransfusionAssociatedCirculatoryOverload: 'Circulatory overload (TACO)',
  TransfusionRelatedAcuteLungInjury: 'Acute lung injury (TRALI)',
  BacterialContamination: 'Bacterial contamination',
  Hypotensive: 'Hypotensive reaction',
  Other: 'Other',
};

export const severityLabel: Record<ReactionSeverity, string> = {
  Mild: 'Mild',
  Moderate: 'Moderate',
  Severe: 'Severe',
  LifeThreatening: 'Life-threatening',
  Death: 'Death',
};

export const severityTone: Record<ReactionSeverity, StatusTone> = {
  Mild: 'informative',
  Moderate: 'warning',
  Severe: 'danger',
  LifeThreatening: 'danger',
  Death: 'danger',
};

export const reactionStatusLabel: Record<ReactionStatus, string> = {
  Reported: 'Reported',
  UnderInvestigation: 'Under investigation',
  Closed: 'Closed',
};

export const reactionStatusTone: Record<ReactionStatus, StatusTone> = {
  Reported: 'warning',
  UnderInvestigation: 'brand',
  Closed: 'success',
};
