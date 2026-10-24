import { describe, expect, it } from 'vitest';
import type { BreachDetail, DataSummary } from './api';
import { breachReportHtml, dataSummaryHtml } from './print';

const contact = {
  title: 'Grievance Officer',
  name: 'Dr <Meera> Iyer',
  email: 'privacy@nana.example',
  phone: '+919876543210',
  address: '1 Hospital Road, Pune',
};

const summary: DataSummary = {
  generatedAt: '2026-10-09T06:30:00Z',
  hospital: 'Nana & Sons Hospital',
  person: {
    uhid: 'NH0000123',
    fullName: 'Asha <b>Rao</b>',
    gender: 'Female',
    dateOfBirth: '1987-03-14',
    phone: '+919812345678',
    alternatePhone: null,
    email: 'asha@example.com',
    address: '12 MG Road, Pune',
    emergencyContact: 'Ravi Rao, husband',
    abhaNumber: 'XX-XXXX-XXXX-1234',
    abhaAddress: null,
    aadhaarLast4: 'XXXX-XXXX-9876',
    hasPhoto: true,
    registeredAt: '2021-01-03T05:00:00Z',
    registeredAtFacility: 'Nana Hospital Pune',
  },
  categories: [
    {
      key: 'registration',
      title: 'Registration details',
      description: 'Your name and contact details.',
      purpose: 'To identify you.',
      basis: 'Needed to care for you.',
      retention: 'At least 3 years after an out-patient visit.',
      retentionKind: 'MedicalRecords',
      items: [],
    },
    {
      key: 'visits',
      title: 'Visits and appointments',
      description: 'When you came.',
      purpose: 'To run your visits.',
      basis: 'Needed to provide care.',
      retention: 'With your medical records.',
      retentionKind: 'MedicalRecords',
      items: [
        {
          key: 'patients.visits',
          label: 'Hospital visits',
          count: 1234,
          first: '2021-01-03T05:00:00Z',
          last: '2026-10-02T05:00:00Z',
        },
      ],
    },
  ],
  consents: [{ type: 'SmsWhatsAppUpdates', granted: true, capturedAt: '2021-01-03T05:00:00Z', revokedAt: null }],
  sharedWith: [],
  processors: [{ name: 'MSG91', role: 'Sends SMS messages', data: 'Your mobile number and the text of each SMS' }],
  nominee: null,
  contact,
  periods: { outpatientYears: 3, inpatientYears: 5, financeYears: 8, messageLogDays: 365, auditYears: 10 },
};

describe('data summary print', () => {
  it('escapes every value and covers each part of the summary', () => {
    const html = dataSummaryHtml(summary, 'PNQDPR2600012', '2026-10-09T07:00:00Z');
    expect(html).toContain('Nana &amp; Sons Hospital');
    expect(html).toContain('Asha &lt;b&gt;Rao&lt;/b&gt;');
    expect(html).not.toContain('<b>Rao</b>');
    expect(html).toContain('Dr &lt;Meera&gt; Iyer, Grievance Officer');
    expect(html).toContain('request PNQDPR2600012');
    expect(html).toContain('+91 98123 45678');
    expect(html).toContain('14 Mar 1987');
    expect(html).toContain('1,234');
    expect(html).toContain('SMS and WhatsApp updates');
    expect(html).toContain('We have not shared your data with any other organisation.');
    expect(html).toContain('MSG91');
    expect(html).toContain('You have not named a nominee.');
    for (const heading of ['What we hold about you', 'Consents', 'Who we shared it with', 'Whom to contact']) {
      expect(html).toContain(heading);
    }
  });
});

const breach: BreachDetail = {
  id: 'b1',
  breachNumber: 'DPB260001',
  title: 'Stolen laptop',
  description: 'A laptop with <discharge> summaries\nwas stolen from the ward.',
  status: 'BoardInformed',
  awareAt: '2026-10-01T04:30:00Z',
  occurredFrom: '2026-09-30T18:30:00Z',
  occurredTo: null,
  nature: ['Confidentiality'],
  location: 'Ward 3',
  dataCategories: ['Identity', 'Health'],
  estimatedAffected: 40,
  consequences: 'Health details could be seen by others.',
  mitigation: 'Passwords changed; laptop wiped remotely.',
  principalSteps: 'Be careful of calls asking for details.',
  cause: null,
  findings: 'A <contractor> left it in a car.',
  remediation: 'Encrypt every laptop.',
  principalNotice: 'Nana Hospitals: a laptop with your discharge summary was stolen.',
  boardInformedAt: '2026-10-01T06:00:00Z',
  reportDueAt: '2026-10-04T04:30:00Z',
  extendedUntil: null,
  reportSubmittedAt: null,
  boardReference: 'DPB/2026/77',
  closedAt: null,
  closingNote: null,
  notifiedAt: '2026-10-02T09:00:00Z',
  reportOverdue: false,
  affectedListed: 38,
  notified: 36,
  withoutContact: 2,
  missingForReport: ['the cause'],
  missingForNotice: [],
  version: 3,
  certInType: null,
  certInReportDueAt: null,
  certInReportedAt: null,
  certInReference: null,
  certInReportOverdue: false,
};

describe('Board report print', () => {
  it('covers every part rule 7(2)(b) asks for, and escapes every value', () => {
    const html = breachReportHtml(
      {
        hospital: 'Nana & Sons Hospital',
        legalName: 'Nana Healthcare Pvt Ltd',
        address: '1 Hospital Road, Pune',
        registrationNumber: 'MH/PUN/123',
        contact,
        breach,
        generatedAt: '2026-10-02T10:00:00Z',
      },
      '2026-10-02T10:05:00Z',
    );
    expect(html).toContain('Nana &amp; Sons Hospital');
    expect(html).toContain('Nana Healthcare Pvt Ltd');
    expect(html).toContain('MH/PUN/123');
    expect(html).toContain('A laptop with &lt;discharge&gt; summaries<br>was stolen from the ward.');
    expect(html).toContain('DPB/2026/77');
    for (const part of [
      'Description of the breach',
      'Nature, extent, timing and location',
      'Likely impact on the people affected',
      'Events, circumstances and reasons that led to the breach',
      'Findings about the person who caused the breach',
      'Measures taken or proposed to reduce the risk',
      'Remedial measures to stop it happening again',
      'Report on the notices to the people affected',
    ]) {
      expect(html).toContain(part);
    }
    // The cause is still empty: the report says so rather than leaving a blank.
    expect(html).toContain('Not recorded yet.');
    expect(html).toContain('A &lt;contractor&gt; left it in a car.');
    // Each part is its own section, in the rule's order.
    expect(html.indexOf('Rule 7(2)(b)(i)<')).toBeLessThan(html.indexOf('Rule 7(2)(b)(ii)'));
    expect(html.indexOf('Rule 7(2)(b)(ii)')).toBeLessThan(html.indexOf('Rule 7(2)(b)(iii)'));
    expect(html.indexOf('Rule 7(2)(b)(iii)')).toBeLessThan(html.indexOf('Rule 7(2)(b)(iv)'));
    expect(html).toContain('Confidentiality');
    expect(html).toContain('Health information');
    expect(html).toContain('38');
    expect(html).toContain('Encrypt every laptop.');
  });
});
