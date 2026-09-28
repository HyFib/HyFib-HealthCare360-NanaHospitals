import { describe, expect, it } from 'vitest';
import type { RadiologyReportView } from './api';
import { radiologyPatientRows, radiologyReportBodyHtml } from './reportBody';

const view: RadiologyReportView = {
  reportId: 'r1',
  imagingStudyId: 's1',
  letterhead: { facilityName: 'Nana Hospitals', address: null, phone: null, email: null, nabhNumber: null, letterheadNote: null, logoFileId: null },
  patient: { id: 'p1', uhid: 'NH26000001', name: 'Ravi Kumar', gender: 'Male', age: '56Y', dateOfBirth: null },
  accessionNumber: 'NHRD260000045',
  studyName: 'CT head (plain)',
  modality: 'CT',
  bodyPart: 'Head',
  referredBy: 'Dr. Meena Iyer',
  orderedAt: '2026-09-26T03:00:00Z',
  acquiredAt: '2026-09-26T04:00:00Z',
  technique: 'Axial sections <5 mm>.',
  clinicalHistory: 'Sudden weakness of the right side.',
  comparison: null,
  findings: 'Hypodensity in the left MCA territory.\n\nNo haemorrhage.',
  impression: 'Acute left MCA infarct.',
  isCriticalFinding: true,
  status: 'Released',
  verifiedAt: '2026-09-26T05:00:00Z',
  releasedAt: '2026-09-26T05:01:00Z',
  signatories: [],
  addenda: [{ id: 'a1', text: 'Compared with MRI: confirmed.', reason: 'MRI done', isCriticalFinding: false, addedAt: '2026-09-27T05:00:00Z', addedBy: 'Dr. Rao' }],
  verificationCode: null,
  verificationPath: null,
};

describe('printed radiology report', () => {
  it('prints the sections in reading order with the critical note and addenda', () => {
    const html = radiologyReportBodyHtml(view);
    expect(html.indexOf('Clinical history')).toBeLessThan(html.indexOf('Findings'));
    expect(html.indexOf('Findings')).toBeLessThan(html.indexOf('Impression'));
    expect(html).toContain('Axial sections &lt;5 mm&gt;.');
    expect(html).toContain('<p>Hypodensity in the left MCA territory.</p><p>No haemorrhage.</p>');
    expect(html).toContain('Critical finding: communicated');
    expect(html).toContain('Compared with MRI: confirmed.');
    expect(html).not.toContain('Comparison</h3>');
  });

  it('lists identity and times', () => {
    const rows = Object.fromEntries(radiologyPatientRows(view));
    expect(rows['Age / sex']).toBe('56Y / M');
    expect(rows['Accession no.']).toBe('NHRD260000045');
    expect(rows['Referred by']).toBe('Dr. Meena Iyer');
  });
});
