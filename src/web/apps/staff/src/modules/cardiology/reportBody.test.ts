import { describe, expect, it } from 'vitest';
import type { CardiologyReportView, EcgFindings } from './api';
import { cardiologyPatientRows, cardiologyReportBodyHtml, measurementRows } from './reportBody';

const ecg: EcgFindings = {
  heartRate: 118,
  rhythm: 'Sinus tachycardia',
  prInterval: 140,
  qrsDuration: 90,
  qtInterval: 320,
  qtcInterval: 449,
  axis: 'Normal',
  stTChanges: 'ST elevation in V1–V4 <2 mm>',
  interpretation: null,
};

describe('cardiology measurements', () => {
  it('lists only the measurements that were recorded, with units', () => {
    const rows = measurementRows('Ecg', { ecg });
    expect(rows[0]).toEqual(['Heart rate', '118 /min']);
    expect(rows.map(([label]) => label)).not.toContain('Interpretation');
    expect(rows).toContainEqual(['QTc', '449 ms']);
  });

  it('formats treadmill, Holter and ABPM values', () => {
    expect(measurementRows('Tmt', { tmt: { protocol: 'Bruce', durationSeconds: 545, mets: 10.1, maxHeartRate: 152, targetHeartRatePercent: 89, bpResponse: null, symptoms: null, stChanges: null, reasonForTermination: 'Fatigue', result: 'Negative' } })).toEqual([
      ['Protocol', 'Bruce'],
      ['Exercise duration', '9:05 min'],
      ['Workload', '10.1 METs'],
      ['Maximum heart rate', '152 /min'],
      ['Target heart rate reached', '89 %'],
      ['Reason for stopping', 'Fatigue'],
      ['Result', 'Negative for inducible ischaemia'],
    ]);
    expect(
      measurementRows('Abpm', { abpm: { daytimeMeanSystolic: 142, daytimeMeanDiastolic: 88, nighttimeMeanSystolic: 130, nighttimeMeanDiastolic: 80, dippingPattern: 'Non-dipper' } }),
    ).toContainEqual(['Daytime mean BP', '142/88 mmHg']);
    expect(measurementRows('Other', { ecg })).toEqual([]);
  });
});

describe('printed cardiology report', () => {
  const view = {
    reportId: 'r',
    cardiologyStudyId: 's',
    letterhead: { facilityName: 'Nana Hospitals', address: null, phone: null, email: null, nabhNumber: null, letterheadNote: null, logoFileId: null },
    patient: { id: 'p', uhid: 'NH26000009', name: 'Sunil Patil', gender: 'Male', age: '61Y', dateOfBirth: null },
    accessionNumber: 'NHCD260000007',
    studyName: 'ECG',
    studyType: 'Ecg',
    referredBy: null,
    orderedAt: '2026-09-26T03:00:00Z',
    acquiredAt: '2026-09-26T03:10:00Z',
    ecg,
    echo: null,
    tmt: null,
    holter: null,
    abpm: null,
    conclusion: 'Acute anterior STEMI pattern.',
    recommendations: 'Urgent cardiology review.',
    isCriticalFinding: true,
    status: 'Released',
    verifiedAt: null,
    releasedAt: '2026-09-26T03:20:00Z',
    signatories: [],
    files: [],
    verificationCode: null,
    verificationPath: null,
    verificationUrl: null,
  } satisfies CardiologyReportView;

  it('prints measurements, conclusion and the critical note, escaped', () => {
    const html = cardiologyReportBodyHtml(view);
    expect(html).toContain('12-lead ECG');
    expect(html).toContain('ST elevation in V1–V4 &lt;2 mm&gt;');
    expect(html).toContain('Critical finding');
    expect(html).toContain('<h3>Conclusion</h3>');
    expect(Object.fromEntries(cardiologyPatientRows(view))['Referred by']).toBe('Self');
  });
});
