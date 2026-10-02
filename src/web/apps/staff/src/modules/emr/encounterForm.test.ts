import { describe, expect, it } from 'vitest';
import {
  addDaysIso,
  appendComplaint,
  applyTemplate,
  daysBetween,
  emptyEncounterForm,
  encounterFormSchema,
  isSignable,
  NOTE_FIELD_LIMITS,
  templateConflicts,
  toContent,
  toFormValues,
  type TemplateContent,
} from './encounterForm';

const today = '2026-09-26';

const template: TemplateContent = {
  chiefComplaint: 'Fever',
  historyOfPresentIllness: 'Fever for __ days, chills: yes/no',
  examination: 'Temp __, throat __',
  assessment: null,
  plan: 'Paracetamol, fluids',
  followUpAdvice: 'Review if fever persists beyond 3 days',
  followUpDays: 3,
};

describe('dates', () => {
  it('adds days across month and year ends', () => {
    expect(addDaysIso('2026-09-26', 7)).toBe('2026-10-03');
    expect(addDaysIso('2026-12-30', 3)).toBe('2027-01-02');
    expect(daysBetween('2026-09-26', '2026-10-03')).toBe(7);
  });
});

describe('schema', () => {
  const schema = encounterFormSchema(today);

  it('accepts an ordinary note', () => {
    expect(
      schema.safeParse({ ...emptyEncounterForm, chiefComplaint: 'Cough', followUpDate: '2026-10-03' }).success,
    ).toBe(true);
  });

  it('rejects a follow-up in the past or beyond two years', () => {
    const past = schema.safeParse({ ...emptyEncounterForm, followUpDate: '2026-09-25' });
    expect(past.success).toBe(false);
    const far = schema.safeParse({ ...emptyEncounterForm, followUpDate: '2029-01-01' });
    expect(far.success).toBe(false);
  });

  it('needs a reason when referring', () => {
    const result = schema.safeParse({ ...emptyEncounterForm, referralExternal: 'AIIMS cardiology' });
    expect(result.success).toBe(false);
    expect(result.error?.issues[0]?.path).toEqual(['referralReason']);
  });

  it('holds each SOAP field to the server’s length limit', () => {
    expect(NOTE_FIELD_LIMITS).toEqual({
      chiefComplaint: 1000,
      historyOfPresentIllness: 8000,
      examination: 8000,
      assessment: 4000,
      plan: 8000,
    });
    for (const [field, limit] of Object.entries(NOTE_FIELD_LIMITS)) {
      expect(schema.safeParse({ ...emptyEncounterForm, [field]: 'a'.repeat(limit) }).success).toBe(true);
      const over = schema.safeParse({ ...emptyEncounterForm, [field]: 'a'.repeat(limit + 1) });
      expect(over.error?.issues[0]?.path).toEqual([field]);
    }
  });
});

describe('mapping', () => {
  it('round-trips content and cleans empty text', () => {
    const values = toFormValues({ chiefComplaint: 'Cough', plan: null, followUpDate: '2026-10-01' });
    expect(values.plan).toBe('');
    const content = toContent({ ...values, examination: '   ', referralReason: 'left over' });
    expect(content).toMatchObject({
      chiefComplaint: 'Cough',
      examination: null,
      followUpDate: '2026-10-01',
      referralReason: null,
    });
  });

  it('keeps the referral reason when a referral is made', () => {
    const content = toContent({
      ...emptyEncounterForm,
      referralDepartmentId: 'dept-1',
      referralReason: ' Chest pain evaluation ',
    });
    expect(content.referralReason).toBe('Chest pain evaluation');
  });

  it('knows when a note can be signed', () => {
    expect(isSignable(emptyEncounterForm)).toBe(false);
    expect(isSignable({ chiefComplaint: '', assessment: 'Viral fever' })).toBe(true);
  });
});

describe('templates', () => {
  it('fills only empty fields by default and sets the follow-up interval', () => {
    const next = applyTemplate({ ...emptyEncounterForm, chiefComplaint: 'High fever' }, template, 'fill-empty', today);
    expect(next.chiefComplaint).toBe('High fever');
    expect(next.plan).toBe('Paracetamol, fluids');
    expect(next.followUpDate).toBe('2026-09-29');
  });

  it('replaces fields on request but keeps a chosen follow-up date', () => {
    const next = applyTemplate(
      { ...emptyEncounterForm, chiefComplaint: 'High fever', followUpDate: '2026-10-10' },
      template,
      'replace',
      today,
    );
    expect(next.chiefComplaint).toBe('Fever');
    expect(next.followUpDate).toBe('2026-10-10');
  });

  it('lists the fields a template would overwrite', () => {
    expect(
      templateConflicts({ ...emptyEncounterForm, chiefComplaint: 'High fever', plan: 'Paracetamol, fluids' }, template),
    ).toEqual(['chiefComplaint']);
  });

  it('appends common complaints without repeats', () => {
    expect(appendComplaint('', 'Fever')).toBe('Fever');
    expect(appendComplaint('Fever', 'Cough')).toBe('Fever, cough');
    expect(appendComplaint('Fever, cough', 'cough')).toBe('Fever, cough');
    expect(appendComplaint('Fever,', 'Body ache')).toBe('Fever, body ache');
  });
});
