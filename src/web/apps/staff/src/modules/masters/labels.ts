import type { Gender } from '@hc360/core';
import type { DepartmentKind, PractitionerKind } from './api';

export const DEPARTMENT_KINDS: readonly DepartmentKind[] = ['Clinical', 'Diagnostic', 'Support', 'Administrative'];

export const DEPARTMENT_KIND_LABELS: Record<DepartmentKind, string> = {
  Clinical: 'Clinical',
  Diagnostic: 'Diagnostic',
  Support: 'Support service',
  Administrative: 'Administrative',
};

export const PRACTITIONER_KINDS: readonly PractitionerKind[] = [
  'Consultant',
  'Visiting',
  'Resident',
  'Nurse',
  'Therapist',
  'Technician',
  'Dietitian',
  'Other',
];

export const PRACTITIONER_KIND_LABELS: Record<PractitionerKind, string> = {
  Consultant: 'Consultant',
  Visiting: 'Visiting consultant',
  Resident: 'Resident doctor',
  Nurse: 'Nurse',
  Therapist: 'Therapist',
  Technician: 'Technician',
  Dietitian: 'Dietitian',
  Other: 'Other',
};

/** Doctors, who need their medical council registration (NMC / state council) to consult and sign. */
export const DOCTOR_KINDS: readonly PractitionerKind[] = ['Consultant', 'Visiting', 'Resident'];

export function isDoctor(kind: PractitionerKind): boolean {
  return DOCTOR_KINDS.includes(kind);
}

export const GENDERS: readonly Gender[] = ['Female', 'Male', 'Other', 'Unknown'];

export const GENDER_LABELS: Record<Gender, string> = {
  Female: 'Female',
  Male: 'Male',
  Other: 'Other',
  Unknown: 'Not stated',
};

export const TITLES = ['Dr.', 'Prof.', 'Mr.', 'Ms.', 'Mrs.'] as const;

/**
 * GST on hospital services: most are exempt (0%). The 2025 rationalisation left 5% and 18%; 12% and 28% remain for
 * services priced before it. The server accepts 0–28%.
 */
export const SERVICE_GST_RATES = [0, 5, 12, 18, 28] as const;

export function gstRateLabel(rate: number): string {
  if (rate === 0) {
    return '0% (exempt)';
  }
  return rate === 12 || rate === 28 ? `${rate}% (older slab)` : `${rate}%`;
}
