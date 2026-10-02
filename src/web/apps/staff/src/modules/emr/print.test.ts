import { describe, expect, it } from 'vitest';
import type { EncounterDetail } from './api';
import { encounterSummaryHtml } from './printTemplates';

describe('encounterSummaryHtml', () => {
  const encounter: EncounterDetail = {
    id: 'e1',
    patientId: 'pt1',
    visitId: null,
    admissionId: null,
    practitionerId: 'p1',
    practitionerName: 'Dr. Asha Rao',
    departmentId: null,
    departmentName: 'General Medicine',
    type: 'OutPatient',
    status: 'InProgress',
    startedAt: '2026-09-26T05:00:00Z',
    chiefComplaint: 'Fever <3 days>',
    historyOfPresentIllness: null,
    examination: 'Throat congested',
    assessment: null,
    plan: null,
    followUpDate: null,
    followUpAdvice: null,
    referralDepartmentId: null,
    referralPractitionerId: null,
    referralExternal: null,
    referralReason: null,
    templateId: null,
    signedAt: null,
    signedByPractitionerId: null,
    amendmentReason: null,
    amendedAt: null,
    amendments: [],
    version: 1,
  };

  it('prints the note and flags an unsigned draft', () => {
    const html = encounterSummaryHtml({
      facilityName: 'Nana Hospitals',
      patient: { fullName: 'Ravi Kumar', uhid: 'NH0001', age: '45Y', gender: 'Male' },
      encounter,
      diagnoses: [],
      vitals: null,
      orders: [],
      prescriptions: [],
      allergies: [],
    });
    expect(html).toContain('<div class="facility">Nana Hospitals</div>');
    expect(html).toContain('Fever &lt;3 days&gt;');
    expect(html).toContain('Throat congested');
    expect(html).toContain('Unsigned draft');
    expect(html).toContain('none recorded');
  });
});
