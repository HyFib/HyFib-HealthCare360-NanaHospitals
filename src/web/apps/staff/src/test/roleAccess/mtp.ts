import { todayIso } from '@hc360/core';
import type { MtpCaseDto, MtpCaseListItem, MtpMonthlyReport, MtpRegister } from '@/modules/mtp/api';
import type { FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Terminations of pregnancy: a case certified and not yet done (the record, so that correcting, recording the
// termination and cancelling are offered), one terminated and one cancelled; the month's register and Form II report.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString();
const today = todayIso();
const [year, month] = [Number(today.slice(0, 4)), Number(today.slice(5, 7))];

const ASHA = { patientId: '0192a000-0000-7000-8000-000000000001', uhid: 'NH26000123', patientName: 'Asha Devi' };
const PRIYA = { patientId: id('a1'), uhid: 'NH26000311', patientName: 'Priya Natarajan' };
const MEENA = { patientId: id('a2'), uhid: 'NH26000342', patientName: 'Meena Kumari' };
const APPROVAL = 'MTP/TN/MDU/2024/017';

const CASES: MtpCaseListItem[] = [
  {
    id: RECORD_ID,
    serialNumber: 'MTP26-00014',
    ...ASHA,
    status: 'Certified',
    weeksOfGestation: 22,
    certifiedAt: daysAgo(0),
    terminatedOn: null,
  },
  {
    id: id('b2'),
    serialNumber: 'MTP26-00013',
    ...PRIYA,
    status: 'Terminated',
    weeksOfGestation: 8,
    certifiedAt: daysAgo(3),
    terminatedOn: daysAgo(2).slice(0, 10),
  },
  {
    id: id('b3'),
    serialNumber: 'MTP26-00012',
    ...MEENA,
    status: 'Cancelled',
    weeksOfGestation: 11,
    certifiedAt: daysAgo(6),
    terminatedOn: null,
  },
];

const CASE: MtpCaseDto = {
  id: RECORD_ID,
  serialNumber: 'MTP26-00014',
  ...ASHA,
  admissionId: id('c1'),
  status: 'Certified',
  approvalNumber: APPROVAL,
  ageYears: 17,
  mentallyIll: false,
  consentBy: 'Guardian',
  guardianName: 'Lakshmi Devi',
  guardianRelation: 'Mother',
  consentOn: today,
  consentFileId: null,
  weeksOfGestation: 22,
  grounds: ['Rape'],
  category: 'Minor',
  medicalBoardReference: null,
  opinions: [
    { doctorName: 'Dr. Kavitha Raman', registrationNumber: 'TNMC 45120', opinionOn: today },
    { doctorName: 'Dr. Anita Rao', registrationNumber: 'TNMC 51877', opinionOn: today },
  ],
  certifiedAt: daysAgo(0),
  certifiedBy: 'Dr. Kavitha Raman',
  terminatedOn: null,
  method: null,
  performedBy: null,
  dischargedOn: null,
  outcome: null,
  remarks: null,
  cancelReason: null,
  version: 3,
};

const REGISTER: MtpRegister = {
  year,
  month,
  approvalNumber: APPROVAL,
  rows: [
    {
      id: id('b2'),
      serialNumber: 'MTP26-00013',
      admittedOn: daysAgo(3).slice(0, 10),
      patientName: PRIYA.patientName,
      uhid: PRIYA.uhid,
      guardianName: 'Ramesh Natarajan',
      ageYears: 31,
      religion: 'Hindu',
      address: '4, Lake View Road, Madurai 625002',
      weeksOfGestation: 8,
      grounds: ['ContraceptiveFailure'],
      category: null,
      terminatedOn: daysAgo(2).slice(0, 10),
      method: 'Medical',
      dischargedOn: daysAgo(2).slice(0, 10),
      outcome: 'Complete; discharged well',
      opinionBy: 'Dr. Kavitha Raman (TNMC 45120)',
      performedBy: 'Dr. Kavitha Raman',
      remarks: null,
    },
    {
      id: RECORD_ID,
      serialNumber: 'MTP26-00014',
      admittedOn: today,
      patientName: ASHA.patientName,
      uhid: ASHA.uhid,
      guardianName: 'Lakshmi Devi',
      ageYears: 17,
      religion: null,
      address: '12, Gandhi Street, Madurai 625001',
      weeksOfGestation: 22,
      grounds: ['Rape'],
      category: 'Minor',
      terminatedOn: null,
      method: null,
      dischargedOn: null,
      outcome: null,
      opinionBy: 'Dr. Kavitha Raman (TNMC 45120); Dr. Anita Rao (TNMC 51877)',
      performedBy: null,
      remarks: null,
    },
  ],
  awaitingTermination: 1,
};

const count = (label: string, n = 0) => ({ label, count: n });

const REPORT: MtpMonthlyReport = {
  year,
  month,
  approvalNumber: APPROVAL,
  total: 1,
  byGestation: [count('Up to 12 weeks', 1), count('13 to 20 weeks'), count('21 to 24 weeks'), count('Beyond 24 weeks')],
  byReligion: [count('Hindu', 1), count('Muslim'), count('Christian'), count('Others')],
  byGround: [
    count('Risk to the life of the pregnant woman'),
    count('Grave injury to her physical health'),
    count('Grave injury to her mental health'),
    count('Pregnancy caused by rape'),
    count('Failure of a contraceptive device or method', 1),
    count('Substantial risk of a serious foetal abnormality'),
  ],
  byMethod: [
    count('Medical method of abortion', 1),
    count('Vacuum aspiration'),
    count('Dilatation and evacuation'),
    count('Other'),
  ],
  byAge: [count('Under 15'), count('15 to 19'), count('20 to 34', 1), count('35 and above')],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/mtp/cases': CASES,
  [`GET /api/v1/mtp/cases/${RECORD_ID}`]: CASE,
  'GET /api/v1/mtp/register': REGISTER,
  'GET /api/v1/mtp/monthly-report': REPORT,
};
