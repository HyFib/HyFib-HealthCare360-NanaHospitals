import type { Body, MortuarySlot } from '@/modules/mortuary/api';
import type { FakeAnswers } from '../fakeApi';

// Mortuary: two slots, one holding an unclaimed medico-legal body, and a released body in the register.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const hoursAgo = (h: number) => new Date(Date.now() - h * 3_600_000).toISOString();

const SLOTS: MortuarySlot[] = [
  {
    id: id('s1'),
    code: 'A-1',
    chamber: 'Chamber A',
    isActive: true,
    bodyId: id('b1'),
    bodyNumber: 'NHMB2600001',
    name: 'Unknown male, about 40',
    receivedAt: hoursAgo(80),
    unclaimed: true,
    version: 1,
  },
  {
    id: id('s2'),
    code: 'A-2',
    chamber: 'Chamber A',
    isActive: true,
    bodyId: null,
    bodyNumber: null,
    name: null,
    receivedAt: null,
    unclaimed: false,
    version: 1,
  },
];

const RELEASED: Body = {
  id: id('b2'),
  number: 'NHMB2600002',
  patientId: id('p1'),
  uhid: 'NH26000201',
  source: 'InHospitalDeath',
  name: 'Murugan S',
  identified: true,
  sex: 'Male',
  ageYears: 74,
  diedAt: hoursAgo(30),
  receivedAt: hoursAgo(29),
  receivedByName: 'Sister Meena',
  broughtBy: 'Ward 4 attender',
  isMlc: false,
  mlcNumber: null,
  policeStation: null,
  identificationMarks: null,
  belongings: 'Gold chain',
  slotCode: 'A-2',
  status: 'Released',
  unclaimed: false,
  releasedAt: hoursAgo(5),
  releasedTo: 'Relatives',
  receivedBy: 'Suresh Kumar',
  relationship: 'Son',
  idProof: 'Aadhaar XXXX-4410',
  policeClearance: null,
  belongingsHandedOver: true,
  releaseNotes: null,
  releasedByName: 'Sister Meena',
  version: 2,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/mortuary/slots': SLOTS,
  'GET /api/v1/mortuary/bodies': [RELEASED],
};
