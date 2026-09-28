import { describe, expect, it } from 'vitest';
import type { LabReportResultLine, LabReportView } from './api';
import { labReportBodyHtml, labReportNotes, labReportPatientRows } from './reportBody';

function result(partial: Partial<LabReportResultLine>): LabReportResultLine {
  return {
    resultId: 'r1',
    name: 'Haemoglobin',
    groupName: null,
    value: '13.2',
    unit: 'g/dL',
    flag: 'Normal',
    referenceText: '13.0 - 17.0',
    status: 'Verified',
    autoValidated: false,
    remarks: null,
    organism: null,
    colonyCount: null,
    susceptibilities: [],
    previousValue: null,
    previousResultAt: null,
    ...partial,
  };
}

const view: LabReportView = {
  reportId: 'rep1',
  labOrderId: 'o1',
  reportNumber: 'NHLR260000045',
  status: 'Released',
  isPartial: true,
  revision: 2,
  letterhead: { facilityName: 'Nana Hospitals', address: null, phone: null, email: null, nabhNumber: null, letterheadNote: null, logoFileId: null },
  patient: { id: 'p1', uhid: 'NH26000001', name: 'Asha Rao', gender: 'Female', age: '31Y', dateOfBirth: null },
  patientClass: 'External',
  referredBy: 'Dr. Kumar',
  clinicalNotes: null,
  orderedAt: '2026-09-26T03:00:00Z',
  firstReleasedAt: '2026-09-26T06:00:00Z',
  releasedAt: '2026-09-26T06:00:00Z',
  sections: [
    {
      section: 'Hematology',
      tests: [
        {
          orderItemId: 'i1',
          code: 'LAB-CBC',
          name: 'Complete blood count',
          status: 'Released',
          specimenType: 'WholeBlood',
          method: 'Impedance',
          interpretation: null,
          referralLabName: null,
          collectedAt: '2026-09-26T03:30:00Z',
          receivedAt: '2026-09-26T03:50:00Z',
          verifiedAt: '2026-09-26T05:50:00Z',
          releasedAt: '2026-09-26T06:00:00Z',
          results: [
            result({ resultId: 'r1', autoValidated: true }),
            result({ resultId: 'r2', name: 'Platelets', value: '42,000', flag: 'CriticalLow', unit: '/µL', referenceText: '1,50,000 - 4,10,000' }),
            result({ resultId: 'r3', name: 'Neutrophils', groupName: 'Differential count', value: '80', flag: 'High', unit: '%' }),
          ],
        },
      ],
    },
    {
      section: 'Microbiology',
      tests: [
        {
          orderItemId: 'i2',
          code: 'LAB-CULTURE-URINE',
          name: 'Urine culture',
          status: 'InLab',
          specimenType: 'Urine',
          method: null,
          interpretation: null,
          referralLabName: 'Metro Reference Lab',
          collectedAt: '2026-09-26T03:35:00Z',
          receivedAt: null,
          verifiedAt: null,
          releasedAt: null,
          results: [
            result({
              resultId: 'r4',
              name: 'Culture',
              value: 'Growth',
              status: 'Entered',
              flag: 'Abnormal',
              organism: 'E. coli',
              colonyCount: '>10^5 CFU/mL',
              susceptibilities: [{ antibiotic: 'Nitrofurantoin', interpretation: 'Sensitive', mic: null }],
            }),
          ],
        },
      ],
    },
  ],
  signatories: [],
  hasAutoValidatedResults: true,
  amendments: [{ revision: 2, reason: 'Transcription error', changes: 'Platelets 4,20,000 → 42,000', amendedAt: '2026-09-26T07:00:00Z', amendedBy: 'Dr. Meena Iyer' }],
  verificationCode: 'ABCD2345EFGH6789JKLM',
  verificationPath: '/api/v1/lab/reports/verify/ABCD2345EFGH6789JKLM',
  verificationUrl: 'https://care.nanahospitals.in/portal/verify/lab/ABCD2345EFGH6789JKLM',
};

describe('printed lab report', () => {
  it('prints sections, flags, groups and auto-validation marks', () => {
    const html = labReportBodyHtml(view);
    expect(html).toContain('<h2>Haematology</h2>');
    expect(html).toContain('13.2*');
    expect(html).toContain('class="crit" title="Critically low">LL');
    expect(html).toContain('<strong>Differential count</strong>');
    expect(html).toContain('Tested at Metro Reference Lab');
    expect(html).toContain('Organism isolated: <strong>E. coli</strong>');
    expect(html).toContain('Sensitive (S)');
    expect(html).toContain('Transcription error');
  });

  it('never prints unverified values on a released-only copy', () => {
    const html = labReportBodyHtml(view, true);
    expect(html).toContain('Awaited');
    expect(html).not.toContain('Growth');
    expect(html).not.toContain('E. coli');
    const staffCopy = labReportBodyHtml(view, false);
    expect(staffCopy).toContain('(not verified)');
  });

  it('lists identity, times and footnotes', () => {
    const rows = Object.fromEntries(labReportPatientRows(view));
    expect(rows['Age / sex']).toBe('31Y / F');
    expect(rows['Report no.']).toBe('NHLR260000045 (revision 2)');
    expect(rows['Referred by']).toBe('Dr. Kumar');
    expect(labReportNotes(view)).toHaveLength(2);
    expect(labReportNotes({ ...view, isPartial: false, hasAutoValidatedResults: false, status: 'Amended' })[0]).toMatch(/amended/);
  });
});
