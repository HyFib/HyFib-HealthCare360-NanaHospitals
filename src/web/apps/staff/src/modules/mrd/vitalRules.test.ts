import { describe, expect, it } from 'vitest';
import {
  ageOn,
  birthProblems,
  causesToSend,
  deathProblems,
  reportProblems,
  womanOfReproductiveAge,
  type BirthDraft,
  type DeathDraft,
} from './vitalRules';

const NOW = '2026-10-25T10:30';

const problemsOf = (problems: Record<string, string | null>) =>
  Object.fromEntries(Object.entries(problems).filter(([, p]) => p !== null));

const birth = (overrides: Partial<BirthDraft> = {}): BirthDraft => ({
  motherPicked: true,
  bornAt: '2026-10-25T06:12',
  outcome: 'LiveBirth',
  sex: 'Female',
  weight: '3100',
  weeks: '39',
  method: 'Natural',
  attendant: 'Doctor',
  motherAge: '27',
  liveChildren: '1',
  informant: 'Dr. Kavitha Raman',
  stillBirthCause: '',
  babyIsMother: false,
  ...overrides,
});

const death = (overrides: Partial<DeathDraft> = {}): DeathDraft => ({
  patientPicked: true,
  diedAt: '2026-10-25T04:40',
  dateOfBirth: '1958-03-14',
  ageYears: '',
  causes: [
    { condition: 'Cardiogenic shock', interval: '6 hours' },
    { condition: 'Acute myocardial infarction', interval: '2 days' },
    { condition: '', interval: '' },
    { condition: '', interval: '' },
  ],
  manner: 'Natural',
  medicoLegal: false,
  pregnancy: 'NotApplicable',
  reproductiveAge: false,
  certifierRegistrationNumber: 'TNMC 45120',
  ...overrides,
});

describe('the birth form (Form 1 and Form 3)', () => {
  it('accepts a live birth as entered', () => {
    expect(problemsOf(birthProblems(birth(), NOW))).toEqual({});
  });

  it('keeps the weight and the gestation within what can be true, and the birth not after now', () => {
    expect(problemsOf(birthProblems(birth({ weight: '250', weeks: '50', bornAt: '2026-10-25T11:00' }), NOW))).toEqual({
      weight: 'Enter the birth weight in grams, from 300 to 7000.',
      weeks: 'Enter the length of the pregnancy in weeks, from 20 to 45.',
      bornAt: 'The birth cannot be after now.',
    });
  });

  it('asks a still birth for at least 28 weeks and the cause of the foetal death', () => {
    expect(problemsOf(birthProblems(birth({ outcome: 'StillBirth', weeks: '26' }), NOW))).toEqual({
      weeks: 'A still birth is a foetus of at least 28 weeks; an earlier loss is an abortion, not reported as a birth.',
      stillBirthCause: 'Enter the cause of the foetal death (Form 3).',
    });
    expect(
      problemsOf(birthProblems(birth({ outcome: 'StillBirth', weeks: '', stillBirthCause: 'Abruptio placentae' }), NOW))
        .weeks,
    ).toContain('at least 28 weeks');
    expect(
      problemsOf(
        birthProblems(birth({ outcome: 'StillBirth', weeks: '34', stillBirthCause: 'Abruptio placentae' }), NOW),
      ),
    ).toEqual({});
  });

  it('asks for the mother, her age, the informant and a baby record that is not hers', () => {
    expect(
      Object.keys(
        problemsOf(
          birthProblems(birth({ motherPicked: false, motherAge: '', informant: ' ', babyIsMother: true }), NOW),
        ),
      ),
    ).toEqual(['mother', 'motherAge', 'informant', 'baby']);
  });
});

describe('the medical certificate of cause of death (Form 4)', () => {
  it('accepts the certificate as entered, and sends Part I without the blank lines after the last', () => {
    expect(problemsOf(deathProblems(death(), NOW))).toEqual({});
    expect(causesToSend(death().causes)).toEqual([
      { condition: 'Cardiogenic shock', interval: '6 hours' },
      { condition: 'Acute myocardial infarction', interval: '2 days' },
    ]);
  });

  it('refuses Part I without line (a) or with a blank line between causes', () => {
    const gap = [
      { condition: 'Septic shock', interval: null },
      { condition: '', interval: null },
      { condition: 'Lobar pneumonia', interval: '5 days' },
    ];
    expect(deathProblems(death({ causes: gap }), NOW).causes).toContain('without blank lines');
    expect(deathProblems(death({ causes: [{ condition: ' ', interval: '' }] }), NOW).causes).toContain('line a');
  });

  it('keeps pending investigation for a medico-legal case', () => {
    expect(deathProblems(death({ manner: 'PendingInvestigation' }), NOW).manner).toBe(
      'Only a medico-legal case waits for the investigation to decide the manner of death.',
    );
    expect(deathProblems(death({ manner: 'PendingInvestigation', medicoLegal: true }), NOW).manner).toBeNull();
  });

  it('asks a woman aged 15 to 49 about pregnancy, and no one else', () => {
    expect(womanOfReproductiveAge('Female', ageOn('1990-05-01', '2026-10-25'))).toBe(true);
    expect(womanOfReproductiveAge('Female', ageOn('1976-10-26', '2026-10-25'))).toBe(true);
    expect(womanOfReproductiveAge('Female', ageOn('1976-10-25', '2026-10-25'))).toBe(false);
    expect(womanOfReproductiveAge('Male', 30)).toBe(false);
    expect(womanOfReproductiveAge('Female', null)).toBeNull();
    expect(deathProblems(death({ reproductiveAge: true }), NOW).pregnancy).toContain('aged 15 to 49');
    expect(deathProblems(death({ reproductiveAge: false, pregnancy: 'Pregnant' }), NOW).pregnancy).toBe(
      'Pregnancy is asked only for a woman aged 15 to 49.',
    );
    expect(deathProblems(death({ reproductiveAge: null, pregnancy: 'Pregnant' }), NOW).pregnancy).toBeNull();
  });

  it('refuses a death after now or before the birth, and asks for the registration number', () => {
    expect(
      problemsOf(deathProblems(death({ diedAt: '2026-10-25T11:00', certifierRegistrationNumber: '' }), NOW)),
    ).toEqual({
      diedAt: 'The death cannot be after now.',
      certifierRegistrationNumber: 'Enter your medical council registration number.',
    });
    expect(deathProblems(death({ diedAt: '1950-01-01T00:00' }), NOW).diedAt).toBe(
      'The death cannot be before the patient’s birth.',
    );
  });
});

describe('the report to the Registrar', () => {
  it('comes on or after the event and not after today, with the registration number', () => {
    expect(
      reportProblems({ reportedOn: '2026-10-20', registrationNumber: 'B-2026-33-01822' }, '2026-10-12', '2026-10-25'),
    ).toEqual({
      reportedOn: null,
      registrationNumber: null,
    });
    expect(reportProblems({ reportedOn: '2026-10-11', registrationNumber: ' ' }, '2026-10-12', '2026-10-25')).toEqual({
      reportedOn: 'The report comes on or after the event, and not after today.',
      registrationNumber: 'Enter the registration number the Registrar gave.',
    });
  });
});
