import { todayIso } from '@hc360/core';
import type { BirthRecordDto, BirthRegister, DeathRecordDto, DeathRegister } from '@/modules/mrd/vital';
import type { FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Births and deaths: the month's registers with an entry not yet reported (the record, so that reporting, correcting
// and marking it entered in error are offered), one reported and one overdue; a live birth and a death certificate.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString();
const dateAgo = (days: number) => daysAgo(days).slice(0, 10);
const today = todayIso();
const [year, month] = [Number(today.slice(0, 4)), Number(today.slice(5, 7))];

const ASHA = { motherPatientId: id('1'), motherUhid: 'NH26000123', motherName: 'Asha Devi' };
const RAVI = { patientId: id('2'), uhid: 'NH26000124', patientName: 'Ravi Shankar' };

const BIRTH: BirthRecordDto = {
  id: RECORD_ID,
  serialNumber: 'B26-00041',
  ...ASHA,
  babyPatientId: null,
  babyUhid: null,
  admissionId: id('ad41'),
  status: 'Recorded',
  bornAt: daysAgo(0),
  outcome: 'LiveBirth',
  sex: 'Female',
  weightGrams: 3100,
  weeksOfGestation: 39,
  method: 'Natural',
  attendant: 'Doctor',
  childName: null,
  fatherName: 'Suresh Kumar',
  motherAgeYears: 27,
  liveChildren: 2,
  informantName: 'Dr. Kavitha Raman',
  stillBirthCause: null,
  dueBy: todayIso(21),
  reportedOn: null,
  registrationNumber: null,
  errorReason: null,
  canMarkInError: true,
  version: 2,
};

const BIRTHS: BirthRegister = {
  year,
  month,
  rows: [
    {
      id: id('b39'),
      serialNumber: 'B26-00039',
      bornAt: daysAgo(24),
      outcome: 'StillBirth',
      sex: 'Male',
      weightGrams: 1650,
      motherName: 'Meena Kumari',
      motherUhid: 'NH26000342',
      fatherName: 'Arjun Kumar',
      childName: null,
      status: 'Recorded',
      dueBy: dateAgo(3),
      overdue: true,
      reportedOn: null,
      registrationNumber: null,
    },
    {
      id: id('b40'),
      serialNumber: 'B26-00040',
      bornAt: daysAgo(6),
      outcome: 'LiveBirth',
      sex: 'Male',
      weightGrams: 2900,
      motherName: 'Priya Natarajan',
      motherUhid: 'NH26000311',
      fatherName: 'Ramesh Natarajan',
      childName: 'Aditya',
      status: 'Reported',
      dueBy: todayIso(15),
      overdue: false,
      reportedOn: dateAgo(2),
      registrationNumber: 'B-2026-33-01822',
    },
    {
      id: RECORD_ID,
      serialNumber: BIRTH.serialNumber,
      bornAt: BIRTH.bornAt,
      outcome: 'LiveBirth',
      sex: 'Female',
      weightGrams: 3100,
      motherName: ASHA.motherName,
      motherUhid: ASHA.motherUhid,
      fatherName: BIRTH.fatherName,
      childName: null,
      status: 'Recorded',
      dueBy: BIRTH.dueBy,
      overdue: false,
      reportedOn: null,
      registrationNumber: null,
    },
  ],
  liveBirths: 2,
  stillBirths: 1,
  overdue: 1,
};

const DEATH: DeathRecordDto = {
  id: RECORD_ID,
  serialNumber: 'D26-00017',
  ...RAVI,
  gender: 'Male',
  admissionId: id('ad17'),
  status: 'Recorded',
  diedAt: daysAgo(1),
  ageYears: 68,
  causes: [
    { condition: 'Cardiogenic shock', interval: '6 hours' },
    { condition: 'Acute myocardial infarction', interval: '2 days' },
    { condition: 'Coronary artery disease', interval: '8 years' },
  ],
  underlyingCause: 'Coronary artery disease',
  otherConditions: 'Type 2 diabetes mellitus',
  manner: 'Natural',
  medicoLegal: false,
  pregnancy: 'NotApplicable',
  certifiedBy: 'Dr. Anil Rao',
  certifierRegistrationNumber: 'MMC 2009/04/1187',
  certifiedAt: daysAgo(1),
  dueBy: todayIso(20),
  reportedOn: null,
  registrationNumber: null,
  errorReason: null,
  canMarkInError: true,
  version: 4,
};

const DEATHS: DeathRegister = {
  year,
  month,
  rows: [
    {
      id: id('d16'),
      serialNumber: 'D26-00016',
      diedAt: daysAgo(9),
      patientName: 'Lakshmi Narayanan',
      uhid: 'NH26000131',
      gender: 'Female',
      ageYears: 81,
      underlyingCause: 'Chronic kidney disease, stage 5',
      manner: 'Natural',
      medicoLegal: false,
      certifiedBy: 'Dr. Anil Rao',
      status: 'Reported',
      dueBy: todayIso(12),
      overdue: false,
      reportedOn: dateAgo(3),
      registrationNumber: 'D-2026-33-00610',
    },
    {
      id: RECORD_ID,
      serialNumber: DEATH.serialNumber,
      diedAt: DEATH.diedAt,
      patientName: RAVI.patientName,
      uhid: RAVI.uhid,
      gender: 'Male',
      ageYears: 68,
      underlyingCause: DEATH.underlyingCause,
      manner: 'Natural',
      medicoLegal: false,
      certifiedBy: DEATH.certifiedBy,
      status: 'Recorded',
      dueBy: DEATH.dueBy,
      overdue: false,
      reportedOn: null,
      registrationNumber: null,
    },
  ],
  medicoLegal: 0,
  overdue: 0,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/mrd/births': BIRTHS,
  [`GET /api/v1/mrd/births/${RECORD_ID}`]: BIRTH,
  'GET /api/v1/mrd/deaths': DEATHS,
  [`GET /api/v1/mrd/deaths/${RECORD_ID}`]: DEATH,
};
