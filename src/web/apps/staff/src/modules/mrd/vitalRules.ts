import type { BirthOutcome, CauseOfDeath, DeathManner, PregnancyAtDeath } from './vital';

// The rules of the Registration of Births and Deaths Act as the birth and death forms hint them (server
// `BirthRecord`, `DeathRecord`, `VitalRegistration`); the server checks them again on every save.

/** An institution reports a birth or death to the Registrar within this many days (RBD Rules, rule 5). */
export const REPORT_WITHIN_DAYS = 21;
/** A foetus born dead from this gestation is a still birth (RBD Act s.2(1)(i)); an earlier loss is not a birth. */
export const STILL_BIRTH_WEEKS = 28;
export const MIN_WEIGHT_GRAMS = 300;
export const MAX_WEIGHT_GRAMS = 7000;
export const MIN_GESTATION_WEEKS = 20;
export const MAX_GESTATION_WEEKS = 45;
/** Part I of the MCCD has four lines, (a) to (d). */
export const MAX_CAUSES = 4;

const whole = (value: string) => (/^\d+$/.test(value.trim()) ? Number(value.trim()) : Number.NaN);

/** An optional whole number as typed: null when blank, NaN when not a whole number. */
export const optionalWhole = (value: string): number | null => (value.trim() ? whole(value) : null);

/** Whole years of age on `on` ("yyyy-MM-dd") for a date of birth ("yyyy-MM-dd"); null without one. */
export function ageOn(dateOfBirth: string | null | undefined, on: string): number | null {
  if (!dateOfBirth || !/^\d{4}-\d{2}-\d{2}/.test(dateOfBirth) || !/^\d{4}-\d{2}-\d{2}/.test(on)) {
    return null;
  }
  const [by, bm, bd] = dateOfBirth.slice(0, 10).split('-').map(Number) as [number, number, number];
  const [y, m, d] = on.slice(0, 10).split('-').map(Number) as [number, number, number];
  return y - by - (m < bm || (m === bm && d < bd) ? 1 : 0);
}

/**
 * Whether the certificate must say if she was pregnant: a woman aged 15 to 49 at death. Null when it is not known (the
 * patient's sex or age not loaded): the form then leaves it to the server.
 */
export function womanOfReproductiveAge(gender: string | null | undefined, age: number | null): boolean | null {
  if (!gender) {
    return null;
  }
  if (gender !== 'Female') {
    return false;
  }
  return age === null || Number.isNaN(age) ? null : age >= 15 && age <= 49;
}

// ------------------------------------------------------------------------------------------------ birth

export interface BirthDraft {
  motherPicked: boolean;
  /** `<input type="datetime-local">` value, hospital time. */
  bornAt: string;
  outcome: BirthOutcome;
  sex: string;
  weight: string;
  weeks: string;
  method: string;
  attendant: string;
  motherAge: string;
  liveChildren: string;
  informant: string;
  stillBirthCause: string;
  /** The baby's own record, when it is the mother's (a mix-up). */
  babyIsMother: boolean;
}

export type BirthField =
  | 'mother'
  | 'bornAt'
  | 'sex'
  | 'weight'
  | 'weeks'
  | 'method'
  | 'attendant'
  | 'motherAge'
  | 'liveChildren'
  | 'informant'
  | 'stillBirthCause'
  | 'baby';

/** Why the birth cannot be saved as entered, by field (null: nothing wrong), as the server words it. */
export function birthProblems(d: BirthDraft, now: string): Record<BirthField, string | null> {
  const weight = optionalWhole(d.weight);
  const weeks = optionalWhole(d.weeks);
  const motherAge = whole(d.motherAge);
  const liveChildren = optionalWhole(d.liveChildren);
  const still = d.outcome === 'StillBirth';
  return {
    mother: d.motherPicked ? null : 'Find the mother.',
    bornAt: !d.bornAt
      ? 'Enter the date and time of the birth.'
      : d.bornAt > now
        ? 'The birth cannot be after now.'
        : null,
    sex: d.sex ? null : 'Choose the sex.',
    weight:
      weight === null || (weight >= MIN_WEIGHT_GRAMS && weight <= MAX_WEIGHT_GRAMS)
        ? null
        : `Enter the birth weight in grams, from ${MIN_WEIGHT_GRAMS} to ${MAX_WEIGHT_GRAMS}.`,
    weeks:
      weeks !== null && !(weeks >= MIN_GESTATION_WEEKS && weeks <= MAX_GESTATION_WEEKS)
        ? `Enter the length of the pregnancy in weeks, from ${MIN_GESTATION_WEEKS} to ${MAX_GESTATION_WEEKS}.`
        : still && (weeks === null || weeks < STILL_BIRTH_WEEKS)
          ? `A still birth is a foetus of at least ${STILL_BIRTH_WEEKS} weeks; an earlier loss is an abortion, not reported as a birth.`
          : null,
    method: d.method ? null : 'Choose the method of delivery.',
    attendant: d.attendant ? null : 'Choose who attended the delivery.',
    motherAge: motherAge >= 10 && motherAge <= 60 ? null : 'Enter the mother’s age in years.',
    liveChildren:
      liveChildren === null || (liveChildren >= 0 && liveChildren <= 20) ? null : 'Enter a number from 0 to 20.',
    informant: d.informant.trim()
      ? null
      : 'Enter who gives the information (the hospital’s officer in charge, or a relative).',
    stillBirthCause: still && !d.stillBirthCause.trim() ? 'Enter the cause of the foetal death (Form 3).' : null,
    baby: d.babyIsMother ? 'The baby has a record of its own.' : null,
  };
}

// ------------------------------------------------------------------------------------------------ death

export interface DeathDraft {
  patientPicked: boolean;
  /** `<input type="datetime-local">` value, hospital time. */
  diedAt: string;
  /** The patient's date of birth ("yyyy-MM-dd"), when known. */
  dateOfBirth: string | null;
  /** Typed for a patient registered without a date of birth. */
  ageYears: string;
  causes: CauseOfDeath[];
  manner: DeathManner | '';
  medicoLegal: boolean;
  pregnancy: PregnancyAtDeath;
  /** From `womanOfReproductiveAge`; null when not known. */
  reproductiveAge: boolean | null;
  certifierRegistrationNumber: string;
}

export type DeathField =
  'patient' | 'diedAt' | 'ageYears' | 'causes' | 'manner' | 'pregnancy' | 'certifierRegistrationNumber';

/** Part I's lines as sent: the lines entered, without the blank ones after the last. */
export function causesToSend(causes: CauseOfDeath[]): CauseOfDeath[] {
  const last = causes.findLastIndex((c) => c.condition.trim() || c.interval?.trim());
  return causes
    .slice(0, last + 1)
    .map((c) => ({ condition: c.condition.trim(), interval: c.interval?.trim() || null }));
}

/** Why the certificate cannot be saved as entered, by field (null: nothing wrong), as the server words it. */
export function deathProblems(d: DeathDraft, now: string): Record<DeathField, string | null> {
  const sent = causesToSend(d.causes);
  const age = optionalWhole(d.ageYears);
  return {
    patient: d.patientPicked ? null : 'Find the patient.',
    diedAt: !d.diedAt
      ? 'Enter the date and time of death.'
      : d.diedAt > now
        ? 'The death cannot be after now.'
        : d.dateOfBirth && d.diedAt.slice(0, 10) < d.dateOfBirth.slice(0, 10)
          ? 'The death cannot be before the patient’s birth.'
          : null,
    ageYears: age === null || (age >= 0 && age <= 130) ? null : 'Enter the age in years, from 0 to 130.',
    causes:
      sent.length === 0 || sent.some((c) => !c.condition)
        ? 'Enter the immediate cause (Part I, line a), and the conditions that led to it in order, without blank lines.'
        : sent.length > MAX_CAUSES
          ? 'Part I has four lines, (a) to (d).'
          : null,
    manner: !d.manner
      ? 'Choose the manner of death.'
      : d.manner === 'PendingInvestigation' && !d.medicoLegal
        ? 'Only a medico-legal case waits for the investigation to decide the manner of death.'
        : null,
    pregnancy:
      d.reproductiveAge === true && d.pregnancy === 'NotApplicable'
        ? 'For a woman aged 15 to 49, say whether she was pregnant or within six weeks of the end of a pregnancy.'
        : d.reproductiveAge === false && d.pregnancy !== 'NotApplicable'
          ? 'Pregnancy is asked only for a woman aged 15 to 49.'
          : null,
    certifierRegistrationNumber: d.certifierRegistrationNumber.trim()
      ? null
      : 'Enter your medical council registration number.',
  };
}

// ------------------------------------------------------------------------------------------------ report

/** The report comes on or after the event's day (hospital time) and not after today; with the registration number. */
export function reportProblems(
  d: { reportedOn: string; registrationNumber: string },
  eventDate: string,
  today: string,
): { reportedOn: string | null; registrationNumber: string | null } {
  return {
    reportedOn:
      !d.reportedOn || d.reportedOn < eventDate || d.reportedOn > today
        ? 'The report comes on or after the event, and not after today.'
        : null,
    registrationNumber: d.registrationNumber.trim() ? null : 'Enter the registration number the Registrar gave.',
  };
}
