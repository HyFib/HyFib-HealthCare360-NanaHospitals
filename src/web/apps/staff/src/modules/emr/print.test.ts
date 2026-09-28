import { describe, expect, it } from 'vitest';
import type { EncounterDetail } from './api';
import type { PrescriptionPrintModel } from './prescriptionsApi';
import { encounterSummaryHtml, prescriptionHtml } from './printTemplates';

const model: PrescriptionPrintModel = {
  id: 'rx1',
  prescriptionNumber: 'NHRX260000123',
  status: 'Signed',
  signedAt: '2026-09-26T05:30:00Z',
  letterhead: null,
  prescriber: {
    practitionerId: 'p1',
    displayName: 'Dr. Asha Rao',
    qualification: 'MBBS, MD (Medicine)',
    registrationCouncil: 'Karnataka Medical Council',
    registrationNumber: 'KMC 12345',
    department: 'General Medicine',
    specialty: null,
    signatureFileId: null,
  },
  patient: { id: 'pt1', uhid: 'NH0001', name: 'Ravi <Kumar>', age: '45Y', gender: 'Male', preferredLanguage: 'Hindi' },
  diagnoses: ['Type 2 diabetes mellitus (E11.9)'],
  knownAllergies: ['Penicillin'],
  lines: [
    {
      sequence: 1,
      drugName: 'Glycomet',
      genericName: 'Metformin',
      strength: '500 mg',
      dosageForm: 'Tablet',
      route: 'Oral',
      dose: '1 tab',
      frequency: 'BD',
      frequencyEnglish: 'Twice a day',
      frequencyHindi: 'दिन में दो बार',
      durationDays: 30,
      quantity: 60,
      instructions: 'After food',
      isControlledDrug: false,
    },
    {
      sequence: 2,
      drugName: 'Alprazolam',
      genericName: null,
      strength: '0.25 mg',
      dosageForm: null,
      route: 'Oral',
      dose: '1 tab',
      frequency: 'HS',
      frequencyEnglish: 'At bedtime',
      frequencyHindi: 'सोते समय',
      durationDays: 7,
      quantity: 7,
      instructions: null,
      isControlledDrug: true,
    },
  ],
  advice: 'Walk 30 minutes daily',
  followUpDate: '2026-10-26',
  followUpAdvice: 'Bring sugar chart',
  refillsAllowed: 0,
  verificationToken: 'tok',
  verificationPath: '/api/v1/public/prescriptions/verify/tok',
  verificationUrl: 'https://care.nanahospitals.in/portal/verify/prescription/tok',
};

describe('prescriptionHtml', () => {
  const html = prescriptionHtml(model, { facilityName: 'Nana Hospitals', qrSvg: '<svg id="qr"></svg>', verifyUrl: 'https://hc360.local/api/v1/public/prescriptions/verify/tok' });

  it('escapes patient data', () => {
    expect(html).toContain('Ravi &lt;Kumar&gt;');
    expect(html).not.toContain('Ravi <Kumar>');
  });

  it('prints bilingual directions, allergies, controlled medicines and the QR code', () => {
    expect(html).toContain('Twice a day');
    expect(html).toContain('दिन में दो बार');
    expect(html).toContain('Known allergies: Penicillin');
    expect(html).toContain('Schedule H1/X');
    expect(html).toContain('<svg id="qr"></svg>');
    expect(html).toContain('KMC 12345');
    expect(html).toContain('30 days');
  });

  it('marks drafts and cancelled prescriptions as not dispensable', () => {
    expect(prescriptionHtml({ ...model, status: 'Cancelled' }, { facilityName: 'Nana Hospitals', qrSvg: null, verifyUrl: null })).toContain('do not dispense');
  });
});

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
    expect(html).toContain('Fever &lt;3 days&gt;');
    expect(html).toContain('Throat congested');
    expect(html).toContain('Unsigned draft');
    expect(html).toContain('none recorded');
  });
});
