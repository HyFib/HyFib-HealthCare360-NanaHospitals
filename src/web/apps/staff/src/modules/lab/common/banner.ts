import type { Gender } from '@hc360/core';
import type { PatientBannerData } from '@hc360/ui';

/** Patient facts every diagnostics DTO carries (order, sample, study). */
export interface DiagnosticsPatient {
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
}

/** Banner data for a diagnostics screen (allergies are not part of diagnostics DTOs, so the banner omits them). */
export function bannerFor(patient: DiagnosticsPatient): PatientBannerData {
  return { id: patient.patientId, uhid: patient.uhid, fullName: patient.patientName, gender: patient.gender, age: patient.age };
}

const SEX_CODES: Record<string, Gender> = { M: 'Male', F: 'Female', O: 'Other', U: 'Unknown' };

/**
 * Parses the compact age the server prints ("34Y 2M", "5M 12D", "9D") to an approximate age in days, used only to
 * preview which reference interval applies while a value is typed (the server decides on save).
 */
export function parseAge(age: string | null | undefined): number | null {
  const text = age?.trim().toUpperCase();
  if (!text || text === '-') {
    return null;
  }
  const match = /^(?:(\d+)Y)?\s*(?:(\d+)M)?\s*(?:(\d+)D)?$/.exec(text);
  if (!match || (!match[1] && !match[2] && !match[3])) {
    return null;
  }
  const years = Number(match[1] ?? 0);
  const months = Number(match[2] ?? 0);
  const days = Number(match[3] ?? 0);
  return Math.round(years * 365.25 + months * 30.44 + days);
}

/** Whole years from the compact age ("34Y 2M" → 34, "5M 12D" → 0). */
export function parseAgeYears(age: string | null | undefined): number | null {
  const days = parseAge(age);
  return days === null ? null : Math.floor(days / 365.25);
}

/** Splits "34Y 2M / M" (labels, result sheets) into age text, approximate days and gender. */
export function parseAgeSex(ageSex: string | null | undefined): { age: string | null; ageDays: number | null; gender: Gender } {
  const [agePart, sexPart] = (ageSex ?? '').split('/').map((part) => part.trim());
  const age = agePart && agePart !== '-' ? agePart : null;
  return { age, ageDays: parseAge(age), gender: SEX_CODES[(sexPart ?? '').toUpperCase()] ?? 'Unknown' };
}
