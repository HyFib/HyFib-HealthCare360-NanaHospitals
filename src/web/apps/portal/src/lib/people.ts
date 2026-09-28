import type { Gender } from '@hc360/core';
import type { MessageKey, Translate } from '@/i18n';

export const GENDER_KEY: Record<Gender, MessageKey> = {
  Male: 'gender.Male',
  Female: 'gender.Female',
  Other: 'gender.Other',
  Unknown: 'gender.Unknown',
};

export interface AgeParts {
  years: number;
  months: number;
  days: number;
}

/** Parses the server's compact age ("34Y 2M", "5M 12D", "9D"); null when absent or unreadable. */
export function parseAge(age: string | null | undefined): AgeParts | null {
  if (!age) {
    return null;
  }
  const part = (unit: 'Y' | 'M' | 'D') => Number(new RegExp(`(\\d+)\\s*${unit}`, 'i').exec(age)?.[1] ?? 0);
  const parts = { years: part('Y'), months: part('M'), days: part('D') };
  return parts.years || parts.months || parts.days || /\b0\s*D\b/i.test(age) ? parts : null;
}

/** "34 yrs" / "5 months" / "9 days" in the patient's language (whole years from one year of age). */
export function ageText(age: string | null | undefined, t: Translate): string | null {
  const parts = parseAge(age);
  if (!parts) {
    return null;
  }
  if (parts.years >= 1) {
    return t('age.years', { count: parts.years });
  }
  if (parts.months >= 1) {
    return t('age.months', { count: parts.months });
  }
  return t('age.days', { count: parts.days });
}

/** "34 yrs · Male" — how a family member is described next to their name. */
export function personLine(person: { age: string | null; gender: Gender }, t: Translate): string {
  return [ageText(person.age, t), person.gender === 'Unknown' ? null : t(GENDER_KEY[person.gender])].filter(Boolean).join(' · ');
}
