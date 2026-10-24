import { describe, expect, it } from 'vitest';
import {
  emptyProcedureForm,
  procedureFields,
  procedureLabel,
  procedureToForm,
  type ProcedureDto,
  type ProcedureFormValues,
} from './procedures';

function dto(partial: Partial<ProcedureDto> = {}): ProcedureDto {
  return {
    id: 'p1',
    patientId: 'pt',
    encounterId: null,
    admissionId: null,
    performedAt: '2026-10-15T04:30:00Z',
    code: '0DTJ4ZZ',
    codeSystem: 'ICD-10-PCS',
    display: 'Laparoscopic appendicectomy',
    performerPractitionerId: 'dr1',
    performerName: 'Dr. Meera Iyer',
    side: 'NotApplicable',
    bodySite: null,
    outcome: 'Uneventful',
    notes: null,
    source: 'Recorded',
    status: 'Completed',
    statusReason: null,
    statusChangedAt: null,
    version: 1,
    ...partial,
  };
}

function form(partial: Partial<ProcedureFormValues> = {}): ProcedureFormValues {
  return { ...emptyProcedureForm('2026-10-15T10:00'), ...partial };
}

describe('procedureFields', () => {
  it('sends the time as IST and blank texts as null', () => {
    expect(
      procedureFields(form({ display: '  Wound suturing ', bodySite: ' ', outcome: 'Haemostasis achieved' })),
    ).toEqual({
      performedAt: '2026-10-15T10:00:00+05:30',
      code: null,
      display: 'Wound suturing',
      performerPractitionerId: null,
      performerName: null,
      side: 'NotApplicable',
      bodySite: null,
      outcome: 'Haemostasis achieved',
      notes: null,
    });
  });

  it('keeps the practitioner unless a performer from elsewhere is named', () => {
    expect(procedureFields(form({ display: 'Lumbar puncture' }), 'dr1').performerPractitionerId).toBe('dr1');
    const external = procedureFields(form({ display: 'Lumbar puncture', performerName: 'Dr. Rao (visiting)' }), 'dr1');
    expect(external.performerPractitionerId).toBeNull();
    expect(external.performerName).toBe('Dr. Rao (visiting)');
  });
});

describe('procedureToForm', () => {
  it('fills the correction form in hospital time', () => {
    expect(procedureToForm(dto())).toEqual({
      performedAt: '2026-10-15T10:00',
      code: '0DTJ4ZZ',
      display: 'Laparoscopic appendicectomy',
      performerName: '',
      side: 'NotApplicable',
      bodySite: '',
      outcome: 'Uneventful',
      notes: '',
    });
  });

  it('lets a performer from elsewhere be edited', () => {
    expect(procedureToForm(dto({ performerPractitionerId: null, performerName: 'Dr. Rao' })).performerName).toBe(
      'Dr. Rao',
    );
  });

  it('round-trips to the same request', () => {
    const p = dto({ side: 'Left', bodySite: 'Index finger', notes: 'Digital block' });
    expect(procedureFields(procedureToForm(p), p.performerPractitionerId)).toEqual({
      performedAt: '2026-10-15T10:00:00+05:30',
      code: '0DTJ4ZZ',
      display: 'Laparoscopic appendicectomy',
      performerPractitionerId: 'dr1',
      performerName: null,
      side: 'Left',
      bodySite: 'Index finger',
      outcome: 'Uneventful',
      notes: 'Digital block',
    });
  });
});

describe('procedureLabel', () => {
  it('shows the code, the procedure and where it was done', () => {
    expect(procedureLabel(dto())).toBe('0DTJ4ZZ Laparoscopic appendicectomy');
    expect(
      procedureLabel(dto({ code: null, display: 'Incision and drainage', side: 'Right', bodySite: 'Axilla' })),
    ).toBe('Incision and drainage · Right, Axilla');
    expect(procedureLabel(dto({ side: 'Bilateral' }))).toBe('0DTJ4ZZ Laparoscopic appendicectomy · Both sides');
  });
});
