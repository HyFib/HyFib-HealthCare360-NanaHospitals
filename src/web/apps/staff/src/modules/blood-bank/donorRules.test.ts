import { describe, expect, it } from 'vitest';
import type { DonorQuestionDto } from './api';
import { ageInYears, evaluateDonor, missingAnswers, vitalChecks, type EligibilityInput } from './donorRules';

const questions: DonorQuestionDto[] = [
  {
    key: 'alcohol_24h',
    text: 'Consumed alcohol in the last 24 hours?',
    onlyFor: null,
    deferralDays: 1,
    permanentDeferral: false,
  },
  {
    key: 'tattoo_12m',
    text: 'Had a tattoo, body piercing or acupuncture in the last 12 months?',
    onlyFor: null,
    deferralDays: 365,
    permanentDeferral: false,
  },
  {
    key: 'pregnancy_12m',
    text: 'Pregnant, breast-feeding, delivered or had an abortion in the last 12 months?',
    onlyFor: 'Female',
    deferralDays: 365,
    permanentDeferral: false,
  },
  {
    key: 'hepatitis_hiv',
    text: 'Ever tested positive for hepatitis B, hepatitis C or HIV?',
    onlyFor: null,
    deferralDays: null,
    permanentDeferral: true,
  },
];

const healthy: EligibilityInput = {
  gender: 'Male',
  dateOfBirth: '1990-05-10',
  isFirstDonation: false,
  lastDonationDate: null,
  vitals: { haemoglobinGdl: 14, weightKg: 70, systolic: 120, diastolic: 80, pulse: 72, temperatureC: 36.8 },
  answers: [
    { key: 'alcohol_24h', yes: false },
    { key: 'tattoo_12m', yes: false },
    { key: 'hepatitis_hiv', yes: false },
  ],
  questions,
  today: '2026-09-26',
};

describe('donor eligibility (mirrors DonorEligibility.Evaluate, NBTC 2017)', () => {
  it('accepts a healthy donor and allows 450 ml from 55 kg', () => {
    expect(evaluateDonor(healthy)).toEqual({
      outcome: 'Eligible',
      reasons: [],
      deferredUntil: null,
      maxBagVolumeMl: 450,
    });
    expect(evaluateDonor({ ...healthy, vitals: { ...healthy.vitals, weightKg: 50 } }).maxBagVolumeMl).toBe(350);
  });

  it('defers for a day on a failed measurement, with the reason', () => {
    const result = evaluateDonor({ ...healthy, vitals: { ...healthy.vitals, haemoglobinGdl: 11.8 } });
    expect(result).toEqual({
      outcome: 'TemporarilyDeferred',
      reasons: ['Haemoglobin 11.8 g/dL is below 12.5 g/dL.'],
      deferredUntil: '2026-09-27',
      maxBagVolumeMl: null,
    });
  });

  it('keeps the whole-blood interval: 90 days for men, 120 for women', () => {
    expect(evaluateDonor({ ...healthy, lastDonationDate: '2026-08-01' })).toMatchObject({
      outcome: 'TemporarilyDeferred',
      deferredUntil: '2026-10-30',
    });
    expect(
      evaluateDonor({
        ...healthy,
        gender: 'Female',
        lastDonationDate: '2026-06-01',
        answers: [...healthy.answers, { key: 'pregnancy_12m', yes: false }],
      }),
    ).toMatchObject({
      deferredUntil: '2026-09-29',
    });
  });

  it('defers by the questionnaire, keeping the latest date', () => {
    const result = evaluateDonor({
      ...healthy,
      answers: [
        { key: 'alcohol_24h', yes: true },
        { key: 'tattoo_12m', yes: true },
        { key: 'hepatitis_hiv', yes: false },
      ],
    });
    expect(result.outcome).toBe('TemporarilyDeferred');
    expect(result.deferredUntil).toBe('2027-09-26');
    expect(result.reasons).toHaveLength(2);
  });

  it('defers permanently for a positive hepatitis or HIV history and for age limits', () => {
    expect(
      evaluateDonor({ ...healthy, answers: [...healthy.answers.slice(0, 2), { key: 'hepatitis_hiv', yes: true }] })
        .outcome,
    ).toBe('PermanentlyDeferred');
    expect(evaluateDonor({ ...healthy, dateOfBirth: '1964-01-01', isFirstDonation: true }).reasons).toContain(
      'First-time donors must be 60 years or younger.',
    );
    expect(evaluateDonor({ ...healthy, dateOfBirth: '1964-01-01' }).outcome).toBe('Eligible');
    expect(evaluateDonor({ ...healthy, dateOfBirth: '2009-01-15' })).toMatchObject({
      outcome: 'TemporarilyDeferred',
      deferredUntil: '2027-01-15',
    });
  });

  it('asks women-only questions only of women', () => {
    expect(missingAnswers(questions, 'Male', healthy.answers)).toEqual([]);
    expect(missingAnswers(questions, 'Female', healthy.answers)).toEqual(['pregnancy_12m']);
  });

  it('shows pass or fail per measurement as it is typed', () => {
    const checks = vitalChecks({
      haemoglobinGdl: 12.5,
      weightKg: 44,
      systolic: null,
      diastolic: 95,
      pulse: 72,
      temperatureC: 37.6,
    });
    expect(Object.fromEntries(checks.map((c) => [c.key, c.ok]))).toEqual({
      haemoglobinGdl: true,
      weightKg: false,
      systolic: null,
      diastolic: false,
      pulse: true,
      temperatureC: false,
    });
  });

  it('counts age in whole years', () => {
    expect(ageInYears('2008-09-27', '2026-09-26')).toBe(17);
    expect(ageInYears('2008-09-26', '2026-09-26')).toBe(18);
  });
});
