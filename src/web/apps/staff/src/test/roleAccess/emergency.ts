import type { ErBoard, MlcRegisterRow } from '@/modules/emergency/api';
import type { FakeAnswers } from '../fakeApi';

// Emergency: two patients on the board (one untriaged, one an MLC waiting for the police) and the MLC register.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();

const BOARD: ErBoard = {
  rows: [
    {
      caseId: id('c1'),
      visitId: id('v1'),
      patientId: id('p1'),
      patientName: 'Senthil Kumar',
      uhid: 'NH26000101',
      gender: 'Male',
      ageYears: 34,
      arrivedAt: minutesAgo(5),
      arrivalMode: 'WalkIn',
      level: null,
      chiefComplaint: 'Chest pain',
      minutesInDepartment: 5,
      doorToTriageMinutes: null,
      doorToDoctorMinutes: null,
      doctorName: null,
      isMlc: false,
      mlcNumber: null,
      mlcIntimationOverdue: false,
      version: 1,
    },
    {
      caseId: id('c2'),
      visitId: id('v2'),
      patientId: id('p2'),
      patientName: 'Lakshmi R',
      uhid: 'NH26000102',
      gender: 'Female',
      ageYears: 27,
      arrivedAt: minutesAgo(80),
      arrivalMode: 'GovernmentAmbulance',
      level: 3,
      chiefComplaint: 'Fall from two-wheeler',
      minutesInDepartment: 80,
      doorToTriageMinutes: 4,
      doorToDoctorMinutes: 20,
      doctorName: 'Dr. Meera Iyer',
      isMlc: true,
      mlcNumber: 'NHMLC2600012',
      mlcIntimationOverdue: true,
      version: 3,
    },
  ],
  untriaged: 1,
  byLevel: [0, 0, 1, 0, 0],
};

const REGISTER: MlcRegisterRow[] = [
  {
    id: id('m1'),
    caseId: id('c2'),
    number: 'NHMLC2600012',
    identifiedAt: minutesAgo(70),
    patientName: 'Lakshmi R',
    uhid: 'NH26000102',
    cause: 'RoadTrafficAccident',
    policeStation: 'Adyar',
    intimatedAt: null,
    receivedBy: null,
    intimationOverdue: true,
    doctorName: 'Dr. Meera Iyer',
  },
];

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/emergency/board': BOARD,
  'GET /api/v1/emergency/mlc': REGISTER,
};
