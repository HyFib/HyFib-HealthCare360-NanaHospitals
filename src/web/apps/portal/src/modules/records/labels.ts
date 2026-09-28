import type { Tone } from '@/components/ListRow';
import type { MessageKey } from '@/i18n';
import type { AllergySeverity, AllergyType, DiagnosisStatus, EncounterType, RecordRequestPurpose } from '@/lib/types';

export const ALLERGY_TYPE: Record<AllergyType, MessageKey> = {
  Drug: 'records.allergy.type.Drug',
  Food: 'records.allergy.type.Food',
  Environmental: 'records.allergy.type.Environmental',
  Other: 'records.allergy.type.Other',
};

export const ALLERGY_SEVERITY: Record<AllergySeverity, { key: MessageKey; tone: Tone }> = {
  Mild: { key: 'records.allergy.severity.Mild', tone: 'neutral' },
  Moderate: { key: 'records.allergy.severity.Moderate', tone: 'warning' },
  Severe: { key: 'records.allergy.severity.Severe', tone: 'danger' },
  Anaphylaxis: { key: 'records.allergy.severity.Anaphylaxis', tone: 'danger' },
};

export const PROBLEM_STATUS: Record<DiagnosisStatus, MessageKey> = {
  Active: 'records.problem.status.Active',
  Chronic: 'records.problem.status.Chronic',
  Resolved: 'records.problem.status.Resolved',
};

export const VISIT_TYPE: Record<EncounterType, MessageKey> = {
  OutPatient: 'records.visit.type.OutPatient',
  InPatient: 'records.visit.type.InPatient',
  Emergency: 'records.visit.type.Emergency',
  Telehealth: 'records.visit.type.Telehealth',
  HealthCheckup: 'records.visit.type.HealthCheckup',
};

/** The purposes a patient can choose (the hospital's other purposes, research and audit, are internal). */
export const REQUEST_PURPOSE: Partial<Record<RecordRequestPurpose, { title: MessageKey; hint: MessageKey }>> = {
  PatientCopy: { title: 'recordRequest.purpose.PatientCopy', hint: 'recordRequest.purpose.PatientCopy.hint' },
  Insurance: { title: 'recordRequest.purpose.Insurance', hint: 'recordRequest.purpose.Insurance.hint' },
  PatientCare: { title: 'recordRequest.purpose.PatientCare', hint: 'recordRequest.purpose.PatientCare.hint' },
  Legal: { title: 'recordRequest.purpose.Legal', hint: 'recordRequest.purpose.Legal.hint' },
  Other: { title: 'recordRequest.purpose.Other', hint: 'recordRequest.purpose.Other.hint' },
};

/** Splits a discharge summary's free-text list (one diagnosis or procedure per line) into its non-empty lines. */
export function splitLines(value: string | null | undefined): string[] {
  return (value ?? '')
    .split(/\r?\n/)
    .map((item) => item.trim())
    .filter(Boolean);
}
