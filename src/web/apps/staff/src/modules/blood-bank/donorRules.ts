import type { Gender } from '@hc360/core';
import { addDays } from '@/modules/ot/shared/datetime';
import type { DonorEligibilityOutcome, DonorQuestionDto, ScreeningAnswer } from './api';

/**
 * Donor selection criteria (NBTC "Guidelines for Blood Donor Selection & Referral" 2017), mirroring the server's
 * `DonorEligibility`. The screening desk shows the result live as values are typed; the server decides and records
 * the outcome.
 */
export const DONOR_RULES = {
  minHaemoglobinGdl: 12.5,
  minWeightKg: 45,
  minWeightFor450Kg: 55,
  minAge: 18,
  maxAge: 65,
  maxAgeFirstDonation: 60,
  minPulse: 60,
  maxPulse: 100,
  minSystolic: 100,
  maxSystolic: 140,
  minDiastolic: 60,
  maxDiastolic: 90,
  maxTemperatureC: 37.5,
  intervalDaysMale: 90,
  intervalDaysFemale: 120,
} as const;

export interface DonorVitals {
  haemoglobinGdl: number;
  weightKg: number;
  systolic: number;
  diastolic: number;
  pulse: number;
  temperatureC: number;
}

export type PartialVitals = { [K in keyof DonorVitals]: number | null };

export interface VitalCheck {
  key: keyof DonorVitals;
  label: string;
  /** null while the value is not entered yet. */
  ok: boolean | null;
  rule: string;
}

/** Pass or fail for each measurement, with the rule in words. */
export function vitalChecks(v: PartialVitals): VitalCheck[] {
  const r = DONOR_RULES;
  const check = (value: number | null, pass: (n: number) => boolean) => (value === null ? null : pass(value));
  return [
    { key: 'haemoglobinGdl', label: 'Haemoglobin', ok: check(v.haemoglobinGdl, (n) => n >= r.minHaemoglobinGdl), rule: `At least ${r.minHaemoglobinGdl} g/dL` },
    { key: 'weightKg', label: 'Weight', ok: check(v.weightKg, (n) => n >= r.minWeightKg), rule: `At least ${r.minWeightKg} kg (${r.minWeightFor450Kg} kg for a 450 ml bag)` },
    { key: 'systolic', label: 'Systolic BP', ok: check(v.systolic, (n) => n >= r.minSystolic && n <= r.maxSystolic), rule: `${r.minSystolic}–${r.maxSystolic} mmHg` },
    { key: 'diastolic', label: 'Diastolic BP', ok: check(v.diastolic, (n) => n >= r.minDiastolic && n <= r.maxDiastolic), rule: `${r.minDiastolic}–${r.maxDiastolic} mmHg` },
    { key: 'pulse', label: 'Pulse', ok: check(v.pulse, (n) => n >= r.minPulse && n <= r.maxPulse), rule: `${r.minPulse}–${r.maxPulse} per minute` },
    { key: 'temperatureC', label: 'Temperature', ok: check(v.temperatureC, (n) => n <= r.maxTemperatureC), rule: `Up to ${r.maxTemperatureC} °C` },
  ];
}

/** Whole years between a date of birth and a day (birthday not yet reached this year → one less). */
export function ageInYears(dateOfBirth: string, today: string): number {
  const [by, bm, bd] = dateOfBirth.split('-').map(Number);
  const [ty, tm, td] = today.split('-').map(Number);
  if (!by || !bm || !bd || !ty || !tm || !td) {
    return 0;
  }
  const beforeBirthday = tm < bm || (tm === bm && td < bd);
  return ty - by - (beforeBirthday ? 1 : 0);
}

function addYears(date: string, years: number): string {
  const [y, m, d] = date.split('-').map(Number);
  const target = new Date(Date.UTC((y ?? 1970) + years, (m ?? 1) - 1, d ?? 1));
  return target.toISOString().slice(0, 10);
}

export function intervalDays(gender: Gender): number {
  return gender === 'Female' ? DONOR_RULES.intervalDaysFemale : DONOR_RULES.intervalDaysMale;
}

/** The questions that apply to this donor (some are only for women). */
export function questionsFor(questions: readonly DonorQuestionDto[], gender: Gender): DonorQuestionDto[] {
  return questions.filter((q) => q.onlyFor === null || q.onlyFor === gender);
}

export function missingAnswers(questions: readonly DonorQuestionDto[], gender: Gender, answers: readonly ScreeningAnswer[]): string[] {
  return questionsFor(questions, gender)
    .filter((q) => !answers.some((a) => a.key === q.key))
    .map((q) => q.key);
}

export interface EligibilityInput {
  gender: Gender;
  dateOfBirth: string;
  isFirstDonation: boolean;
  lastDonationDate: string | null;
  vitals: DonorVitals;
  answers: readonly ScreeningAnswer[];
  questions: readonly DonorQuestionDto[];
  today: string;
}

export interface EligibilityPreview {
  outcome: DonorEligibilityOutcome;
  reasons: string[];
  deferredUntil: string | null;
  maxBagVolumeMl: number | null;
}

/** Same checks, wording and deferral dates as `DonorEligibility.Evaluate`. */
export function evaluateDonor(input: EligibilityInput): EligibilityPreview {
  const r = DONOR_RULES;
  const reasons: string[] = [];
  let permanent = false;
  let until: string | null = null;
  const tomorrow = addDays(input.today, 1);
  const deferUntil = (date: string, reason: string) => {
    reasons.push(reason);
    until = until === null || date > until ? date : until;
  };

  const age = ageInYears(input.dateOfBirth, input.today);
  const maxAge = input.isFirstDonation ? r.maxAgeFirstDonation : r.maxAge;
  if (age < r.minAge) {
    deferUntil(addYears(input.dateOfBirth, r.minAge), `Donors must be at least ${r.minAge} years old.`);
  } else if (age > maxAge) {
    permanent = true;
    reasons.push(input.isFirstDonation ? `First-time donors must be ${r.maxAgeFirstDonation} years or younger.` : `Donors must be ${r.maxAge} years or younger.`);
  }

  if (input.lastDonationDate) {
    const days = intervalDays(input.gender);
    const next = addDays(input.lastDonationDate, days);
    if (input.today < next) {
      deferUntil(next, `Minimum interval between whole-blood donations is ${days} days.`);
    }
  }

  const v = input.vitals;
  if (v.haemoglobinGdl < r.minHaemoglobinGdl) {
    deferUntil(tomorrow, `Haemoglobin ${v.haemoglobinGdl} g/dL is below ${r.minHaemoglobinGdl} g/dL.`);
  }
  if (v.weightKg < r.minWeightKg) {
    deferUntil(tomorrow, `Weight ${v.weightKg} kg is below ${r.minWeightKg} kg.`);
  }
  if (v.pulse < r.minPulse || v.pulse > r.maxPulse) {
    deferUntil(tomorrow, `Pulse ${v.pulse}/min is outside ${r.minPulse}–${r.maxPulse}/min.`);
  }
  if (v.systolic < r.minSystolic || v.systolic > r.maxSystolic || v.diastolic < r.minDiastolic || v.diastolic > r.maxDiastolic) {
    deferUntil(
      tomorrow,
      `Blood pressure ${v.systolic}/${v.diastolic} mmHg is outside ${r.minSystolic}–${r.maxSystolic}/${r.minDiastolic}–${r.maxDiastolic} mmHg.`,
    );
  }
  if (v.temperatureC > r.maxTemperatureC) {
    deferUntil(tomorrow, `Temperature ${v.temperatureC} °C is above ${r.maxTemperatureC} °C.`);
  }

  for (const question of questionsFor(input.questions, input.gender)) {
    if (!input.answers.some((a) => a.key === question.key && a.yes)) {
      continue;
    }
    if (question.deferralDays !== null) {
      deferUntil(addDays(input.today, question.deferralDays), question.text);
    } else {
      permanent = true;
      reasons.push(question.text);
    }
  }

  if (permanent) {
    return { outcome: 'PermanentlyDeferred', reasons, deferredUntil: null, maxBagVolumeMl: null };
  }
  return reasons.length > 0
    ? { outcome: 'TemporarilyDeferred', reasons, deferredUntil: until, maxBagVolumeMl: null }
    : { outcome: 'Eligible', reasons, deferredUntil: null, maxBagVolumeMl: v.weightKg >= r.minWeightFor450Kg ? 450 : 350 };
}
