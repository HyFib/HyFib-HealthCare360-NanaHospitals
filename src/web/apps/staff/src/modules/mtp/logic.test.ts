import { describe, expect, it } from 'vitest';
import { certificationProblems, consentByFor, gestationRule, opinionsNeeded, type CertificationDraft } from './logic';

const TODAY = '2026-10-25';

const draft = (overrides: Partial<CertificationDraft> = {}): CertificationDraft => ({
  ageYears: 28,
  mentallyIll: false,
  guardianName: '',
  guardianRelation: '',
  consentOn: TODAY,
  weeksOfGestation: 9,
  grounds: ['ContraceptiveFailure'],
  category: null,
  medicalBoardReference: '',
  opinions: [{ doctorName: 'Dr. Kavitha Raman', registrationNumber: 'TNMC 45120', opinionOn: TODAY }],
  ...overrides,
});

const second = { doctorName: 'Dr. Anita Rao', registrationNumber: 'TNMC 51877', opinionOn: TODAY };

const problemsOf = (d: CertificationDraft) =>
  Object.fromEntries(Object.entries(certificationProblems(d, TODAY)).filter(([, p]) => p !== null));

describe('the MTP case form', () => {
  it('accepts an adult up to 20 weeks with one doctor’s opinion and her own consent', () => {
    expect(problemsOf(draft())).toEqual({});
    expect(consentByFor(28, false)).toBe('Woman');
  });

  it('asks for the guardian’s consent for a minor or a mentally ill woman', () => {
    expect(consentByFor(17, false)).toBe('Guardian');
    expect(consentByFor(30, true)).toBe('Guardian');
    expect(problemsOf(draft({ ageYears: 16 }))).toEqual({
      guardian: 'Enter the guardian’s name and relation to the woman.',
    });
    expect(problemsOf(draft({ ageYears: 16, guardianName: 'Lakshmi', guardianRelation: 'Mother' }))).toEqual({});
  });

  it('needs two doctors and a rule 3B category from 21 to 24 weeks, without contraceptive failure', () => {
    expect(opinionsNeeded(20)).toBe(1);
    expect(opinionsNeeded(21)).toBe(2);
    expect(problemsOf(draft({ weeksOfGestation: 22 }))).toEqual({
      grounds: 'The failure of a contraceptive is a ground up to 20 weeks only.',
      category: 'From 20 to 24 weeks the woman must be in a category of rule 3B. Choose it.',
      opinions: 'Beyond 20 weeks the opinion of two doctors is needed.',
    });
    expect(
      problemsOf(
        draft({
          weeksOfGestation: 22,
          grounds: ['Rape'],
          category: 'SexualAssaultRapeOrIncest',
          opinions: [draft().opinions[0]!, second],
        }),
      ),
    ).toEqual({});
  });

  it('allows beyond 24 weeks only a foetal abnormality with the Medical Board’s reference', () => {
    expect(
      problemsOf(draft({ weeksOfGestation: 26, grounds: ['RiskToLife'], opinions: [draft().opinions[0]!, second] })),
    ).toEqual({
      grounds: 'Beyond 24 weeks a pregnancy is terminated only for a substantial foetal abnormality.',
      medicalBoardReference: 'Beyond 24 weeks enter the reference of the Medical Board’s opinion.',
    });
    expect(
      problemsOf(
        draft({
          weeksOfGestation: 26,
          grounds: ['FoetalAbnormality'],
          medicalBoardReference: 'SMB/TN/2026/118',
          opinions: [draft().opinions[0]!, second],
        }),
      ),
    ).toEqual({});
    expect(gestationRule(26)).toContain('Medical Board');
  });

  it('refuses the same doctor twice and dates after today', () => {
    const same = { ...second, registrationNumber: 'tnmc 45120' };
    expect(problemsOf(draft({ opinions: [draft().opinions[0]!, same] })).opinions).toBe(
      'The two opinions must be of two different doctors.',
    );
    expect(problemsOf(draft({ consentOn: '2026-10-26' })).consentOn).toBe('The consent cannot be dated after today.');
  });
});
