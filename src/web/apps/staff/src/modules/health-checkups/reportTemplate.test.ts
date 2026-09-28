import { describe, expect, it } from 'vitest';
import type { CheckupReport } from './api';
import { checkupReportHtml } from './reportTemplate';

const report: CheckupReport = {
  booking: {
    id: 'b1',
    bookingNumber: 'NHHC26000042',
    patientId: 'p1',
    uhid: 'NH0001',
    patientName: 'Meena Iyer',
    packageId: 'pk1',
    packageCode: 'HC-EXEC',
    packageName: 'Executive health check',
    scheduledDate: '2026-09-27',
    status: 'Completed',
    visitId: null,
    orderId: 'o1',
    corporateBatchId: null,
    corporateName: 'Acme <Pvt> Ltd',
    notes: null,
    checkedInAt: '2026-09-27T03:00:00Z',
    summary: 'Mild dyslipidaemia.\nOtherwise normal.',
    recommendations: 'Diet control; repeat lipid profile in 3 months',
    reviewedByPractitionerId: 'd1',
    reviewedAt: '2026-09-27T08:00:00Z',
    completedAt: '2026-09-27T08:30:00Z',
    cancelReason: null,
    itinerary: [
      { id: 's2', sequence: 2, departmentId: null, departmentName: 'Radiology', description: 'Chest X-ray', status: 'Skipped', completedAt: null, notes: 'Pregnancy not ruled out' },
      { id: 's1', sequence: 1, departmentId: null, departmentName: 'Laboratory', description: 'Blood and urine samples', status: 'Done', completedAt: null, notes: null },
    ],
    version: 3,
  },
  patient: {
    id: 'p1',
    uhid: 'NH0001',
    fullName: 'Meena Iyer',
    gender: 'Female',
    dateOfBirth: '1980-01-01',
    age: '46Y',
    phone: null,
    bloodGroup: 'OPositive',
    category: 'Corporate',
    isVip: false,
    isDeceased: false,
    photoFileId: null,
  },
  letterhead: null,
  reviewingDoctor: {
    practitionerId: 'd1',
    displayName: 'Dr. Arjun Menon',
    qualification: 'MD',
    registrationCouncil: 'TCMC',
    registrationNumber: '12345',
    department: null,
    specialty: null,
    signatureFileId: null,
  },
  order: null,
};

describe('checkupReportHtml', () => {
  it('prints the summary, stations in order and the doctor, escaping values', () => {
    const html = checkupReportHtml(report, 'Nana Hospitals');
    expect(html).toContain('Mild dyslipidaemia.');
    expect(html).toContain('Acme &lt;Pvt&gt; Ltd');
    expect(html.indexOf('Blood and urine samples')).toBeLessThan(html.indexOf('Chest X-ray'));
    expect(html).toContain('Not done');
    expect(html).toContain('Dr. Arjun Menon');
    expect(html).not.toContain('not final');
  });

  it('marks an unfinished checkup as not final', () => {
    expect(checkupReportHtml({ ...report, booking: { ...report.booking, status: 'InProgress', summary: null } }, 'Nana Hospitals')).toContain('In progress — not final');
  });
});
