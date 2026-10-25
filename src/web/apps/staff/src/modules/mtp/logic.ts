import type { MtpConsentBy, MtpGround, MtpOpinion, MtpRule3BCategory } from './api';

// The MTP Act's rules as the case form hints them (server `MtpCase`); the server checks them again on every save.

/** Up to this, one doctor's opinion is enough (s.3(2)(a)). */
export const ONE_OPINION_WEEKS = 20;
/** Up to this, two doctors' opinions for a woman in a rule 3B category (s.3(2)(b)). */
export const TWO_OPINIONS_WEEKS = 24;
/** The longest gestation a case may give: beyond it, the period was mis-typed. */
export const MAX_WEEKS_OF_GESTATION = 42;
export const AGE_OF_MAJORITY = 18;
/** At most three doctors are entered on Form I. */
export const MAX_OPINIONS = 3;

/** The number of doctors' opinions the length of a pregnancy calls for (beyond 24 weeks, with the Medical Board's). */
export const opinionsNeeded = (weeks: number) => (weeks > ONE_OPINION_WEEKS ? 2 : 1);

/** s.3(4): a minor's or a mentally ill woman's consent is her guardian's; anyone else's is her own. */
export const consentByFor = (ageYears: number, mentallyIll: boolean): MtpConsentBy =>
  (Number.isFinite(ageYears) && ageYears < AGE_OF_MAJORITY) || mentallyIll ? 'Guardian' : 'Woman';

/** What the law asks for at this length of pregnancy, for the form's hint. */
export function gestationRule(weeks: number): string {
  if (!Number.isInteger(weeks) || weeks < 1) {
    return 'Up to 20 weeks one doctor’s opinion; from 21 to 24 weeks two doctors and a rule 3B category; beyond 24 weeks only a foetal abnormality the Medical Board has diagnosed.';
  }
  if (weeks <= ONE_OPINION_WEEKS) {
    return 'Up to 20 weeks: the opinion of one doctor.';
  }
  if (weeks <= TWO_OPINIONS_WEEKS) {
    return 'From 21 to 24 weeks: the opinion of two doctors, for a woman in a category of rule 3B. Failure of a contraceptive is not a ground.';
  }
  return 'Beyond 24 weeks: only for a substantial foetal abnormality, on the Medical Board’s opinion.';
}

export interface CertificationDraft {
  ageYears: number;
  mentallyIll: boolean;
  guardianName: string;
  guardianRelation: string;
  consentOn: string;
  weeksOfGestation: number;
  grounds: MtpGround[];
  category: MtpRule3BCategory | null;
  medicalBoardReference: string;
  opinions: MtpOpinion[];
}

export type CertificationField =
  | 'ageYears'
  | 'guardian'
  | 'consentOn'
  | 'weeksOfGestation'
  | 'grounds'
  | 'category'
  | 'medicalBoardReference'
  | 'opinions';

/** Why the consent and the opinion cannot be saved as entered, by field (null: nothing wrong), as the server words it. */
export function certificationProblems(d: CertificationDraft, today: string): Record<CertificationField, string | null> {
  const weeksValid = Number.isInteger(d.weeksOfGestation) && d.weeksOfGestation >= 1;
  const weeks = weeksValid ? d.weeksOfGestation : 0;
  const filled = d.opinions.filter((o) => o.doctorName.trim() && o.registrationNumber.trim());
  const registrations = filled.map((o) => o.registrationNumber.trim().toUpperCase());
  const needed = opinionsNeeded(weeks);
  const guardian = consentByFor(d.ageYears, d.mentallyIll) === 'Guardian';

  return {
    ageYears:
      Number.isInteger(d.ageYears) && d.ageYears >= 10 && d.ageYears <= 60 ? null : 'Enter the woman’s age in years.',
    guardian:
      !guardian || (d.guardianName.trim() && d.guardianRelation.trim())
        ? null
        : 'Enter the guardian’s name and relation to the woman.',
    consentOn: !d.consentOn
      ? 'Enter the date of the consent.'
      : d.consentOn > today
        ? 'The consent cannot be dated after today.'
        : null,
    weeksOfGestation:
      weeksValid && d.weeksOfGestation <= MAX_WEEKS_OF_GESTATION
        ? null
        : `Enter the length of the pregnancy in weeks, from 1 to ${MAX_WEEKS_OF_GESTATION}.`,
    grounds:
      d.grounds.length === 0
        ? 'Choose the grounds on which the opinion was formed.'
        : d.grounds.includes('ContraceptiveFailure') && weeks > ONE_OPINION_WEEKS
          ? 'The failure of a contraceptive is a ground up to 20 weeks only.'
          : weeks > TWO_OPINIONS_WEEKS && !(d.grounds.length === 1 && d.grounds[0] === 'FoetalAbnormality')
            ? 'Beyond 24 weeks a pregnancy is terminated only for a substantial foetal abnormality.'
            : null,
    category:
      weeks > ONE_OPINION_WEEKS && weeks <= TWO_OPINIONS_WEEKS && !d.category
        ? 'From 20 to 24 weeks the woman must be in a category of rule 3B. Choose it.'
        : null,
    medicalBoardReference:
      weeks > TWO_OPINIONS_WEEKS && !d.medicalBoardReference.trim()
        ? 'Beyond 24 weeks enter the reference of the Medical Board’s opinion.'
        : null,
    opinions:
      filled.length !== d.opinions.length
        ? 'Enter each doctor’s name and registration number.'
        : filled.length < needed
          ? needed === 1
            ? 'Enter the doctor who formed the opinion.'
            : 'Beyond 20 weeks the opinion of two doctors is needed.'
          : new Set(registrations).size !== registrations.length
            ? 'The two opinions must be of two different doctors.'
            : d.opinions.some((o) => !o.opinionOn || o.opinionOn > today)
              ? 'Enter the date of each opinion, not after today.'
              : null,
  };
}
