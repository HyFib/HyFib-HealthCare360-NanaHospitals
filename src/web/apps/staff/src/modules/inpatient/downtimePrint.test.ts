import { describe, expect, it } from 'vitest';
import { downtimeBody } from './downtimePrint';
import type { DowntimePatient, DowntimeWardPack } from './wardCareApi';
import { isCriticalCareWard, news2Advice, news2Label, news2Tone } from './wardCareApi';

const patient = (overrides: Partial<DowntimePatient> = {}): DowntimePatient => ({
  admissionId: 'a1',
  admissionNumber: 'NHIP26000012',
  uhid: 'NH000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
  bedLabel: 'FMW-01',
  consultantName: 'Dr. Meera Iyer',
  admittedAt: '2026-11-06T04:30:00Z',
  dayOfStay: 3,
  provisionalDiagnosis: 'Pneumonia',
  isMlc: false,
  status: 'Admitted',
  allergyStatus: 'Recorded',
  allergies: [{ allergen: 'Penicillin', severity: 'Severe', reaction: 'Urticaria' }],
  medications: [
    {
      orderId: 'o1',
      drugName: 'Ceftriaxone 1 g IV',
      dose: '1 g',
      route: 'IV',
      frequency: 'BD',
      isAsNeeded: false,
      isHighAlert: true,
      isControlledDrug: false,
      dosesHeld: false,
      dueTimes: ['2026-11-08T14:30:00Z'],
    },
    {
      orderId: 'o2',
      drugName: 'Paracetamol 500 mg',
      dose: '500 mg',
      route: 'PO',
      frequency: 'SOS',
      isAsNeeded: true,
      isHighAlert: false,
      isControlledDrug: false,
      dosesHeld: false,
      dueTimes: [],
    },
  ],
  news2: { score: 6, response: 'Urgent', isComplete: true, recordedAt: '2026-11-08T10:00:00Z' },
  pendingTasks: ['NEWS2 6: escalated at 15:30, waiting for a response'],
  carePlan: [
    { title: 'Risk of falls', goal: 'No fall', interventions: [{ text: 'Rails up', frequency: 'Every shift' }] },
  ],
  ...overrides,
});

const pack = (patients: DowntimePatient[]): DowntimeWardPack => ({
  generatedAt: '2026-11-08T10:30:00Z',
  windowEndsAt: '2026-11-08T18:30:00Z',
  facilityName: 'Nana Hospitals',
  printedByName: 'Sr. Latha',
  wards: [
    {
      wardId: 'w1',
      code: 'FMW',
      name: 'Female medical ward',
      type: 'General',
      counts: { total: 12, occupied: 1, vacant: 10, reserved: 1, cleaning: 0, blocked: 0, maintenance: 0 },
      patients,
    },
  ],
});

describe('downtime ward sheet', () => {
  it('prints the census and each patient with allergies, medicines due, NEWS2, tasks and the care plan', () => {
    const html = downtimeBody(pack([patient()]));
    expect(html).toContain('Census: 1 occupied of 12 beds');
    expect(html).toContain('ALLERGY:</strong> Penicillin (Severe, Urticaria)');
    expect(html).toContain('Ceftriaxone 1 g IV');
    expect(html).toContain('High alert');
    expect(html).toContain('As needed');
    expect(html).toContain('NEWS2 6');
    expect(html).toContain('URGENT REVIEW');
    expect(html).toContain('waiting for a response');
    expect(html).toContain('Risk of falls');
  });

  it('warns when nobody has recorded allergies, and escapes every value', () => {
    const html = downtimeBody(
      pack([patient({ allergyStatus: 'NotRecorded', allergies: [], patientName: '<img src=x onerror=alert(1)>' })]),
    );
    expect(html).toContain('ALLERGIES NOT RECORDED');
    expect(html).not.toContain('<img');
    expect(html).toContain('&lt;img');
  });
});

describe('NEWS2 display', () => {
  it('tones and labels the score', () => {
    expect(news2Tone('Emergency')).toBe('danger');
    expect(news2Tone('Urgent')).toBe('warning');
    expect(news2Tone('Routine')).toBe('informative');
    expect(news2Label(6, true)).toBe('NEWS2 6');
    expect(news2Label(4, false)).toBe('NEWS2 4 (incomplete)');
    expect(news2Advice('Emergency')).toMatch(/rapid response/);
  });

  it('treats ICU, NICU, PICU and HDU as critical care for an urgent move', () => {
    expect(['Icu', 'Nicu', 'Picu', 'Hdu'].every((t) => isCriticalCareWard(t as never))).toBe(true);
    expect(isCriticalCareWard('General')).toBe(false);
    expect(isCriticalCareWard(null)).toBe(false);
  });
});
