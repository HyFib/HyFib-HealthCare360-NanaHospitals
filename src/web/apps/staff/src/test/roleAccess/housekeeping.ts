import type {
  CleaningArea,
  CleaningBoard,
  CleaningRecord,
  CleaningRequest,
  HousekeepingReport,
} from '@/modules/housekeeping/api';
import type { FakeAnswers } from '../fakeApi';

// Housekeeping: the ICU cleaned three times a day and the OPD corridor daily, today's board with one cleaning waiting
// for a supervisor, a spill answered late and waiting to be verified, a terminal cleaning asked for, and the report.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();
const today = new Date().toISOString().slice(0, 10);

const ICU_CHECKLIST = ['Bed rails and monitors wiped', 'Floor mopped with 1% hypochlorite', 'Bins emptied'];

const AREAS: CleaningArea[] = [
  {
    id: id('h1'),
    code: 'ICU',
    name: 'Medical ICU',
    wardId: id('w9'),
    wardName: 'Medical ICU',
    riskZone: 'High',
    frequency: 'Daily',
    timesPerDay: 3,
    weekDays: [],
    checklist: ICU_CHECKLIST,
    isActive: true,
    version: 2,
  },
  {
    id: id('h2'),
    code: 'OPD-COR',
    name: 'OPD corridor',
    wardId: null,
    wardName: null,
    riskZone: 'Low',
    frequency: 'Daily',
    timesPerDay: 1,
    weekDays: [],
    checklist: ['Floor mopped', 'Chairs wiped'],
    isActive: true,
    version: 1,
  },
];

const ICU_MORNING: CleaningRecord = {
  id: id('hc1'),
  areaId: id('h1'),
  date: today,
  slot: 1,
  cleanedAt: minutesAgo(180),
  cleanedByName: 'Selvi (housekeeping)',
  checklist: ICU_CHECKLIST.map((item) => ({ item, done: true, note: null })),
  notes: null,
  status: 'AwaitingVerification',
  verifiedAt: null,
  verifiedByName: null,
  verificationRemarks: null,
  cleanedByYou: false,
  version: 1,
};

const BOARD: CleaningBoard = {
  date: today,
  slots: [
    {
      areaId: id('h1'),
      areaCode: 'ICU',
      areaName: 'Medical ICU',
      wardName: 'Medical ICU',
      riskZone: 'High',
      slot: 1,
      checklist: ICU_CHECKLIST,
      record: ICU_MORNING,
    },
    {
      areaId: id('h1'),
      areaCode: 'ICU',
      areaName: 'Medical ICU',
      wardName: 'Medical ICU',
      riskZone: 'High',
      slot: 2,
      checklist: ICU_CHECKLIST,
      record: null,
    },
    {
      areaId: id('h2'),
      areaCode: 'OPD-COR',
      areaName: 'OPD corridor',
      wardName: null,
      riskZone: 'Low',
      slot: 1,
      checklist: ['Floor mopped', 'Chairs wiped'],
      record: null,
    },
  ],
  due: 3,
  notDone: 2,
  awaitingVerification: 1,
  redo: 0,
};

const SPILL_ITEMS = [
  'Area cordoned off',
  'Spill absorbed and discarded as yellow',
  'Surface wiped with 1% hypochlorite',
];

const REQUESTS: CleaningRequest[] = [
  {
    id: id('hr1'),
    kind: 'Spill',
    wardId: id('w1'),
    wardName: 'General ward (male)',
    location: 'Bed 7, blood spill',
    details: null,
    requestedAt: minutesAgo(70),
    requestedByName: 'Staff nurse Kavitha',
    status: 'AwaitingVerification',
    targetMinutes: 15,
    acknowledgedAt: minutesAgo(48),
    acknowledgedByName: 'Selvi (housekeeping)',
    responseMinutes: 22,
    responseLate: true,
    completedAt: minutesAgo(35),
    completedByName: 'Selvi (housekeeping)',
    completionMinutes: 35,
    checklistItems: SPILL_ITEMS,
    checklist: SPILL_ITEMS.map((item) => ({ item, done: true, note: null })),
    notes: null,
    verifiedAt: null,
    verifiedByName: null,
    verificationRemarks: null,
    completedByYou: false,
    version: 3,
  },
  {
    id: id('hr2'),
    kind: 'Terminal',
    wardId: id('w1'),
    wardName: 'General ward (male)',
    location: 'Isolation room 2 after discharge',
    details: 'Patient was MRSA positive',
    requestedAt: minutesAgo(20),
    requestedByName: 'Staff nurse Kavitha',
    status: 'Open',
    targetMinutes: 60,
    acknowledgedAt: null,
    acknowledgedByName: null,
    responseMinutes: null,
    responseLate: false,
    completedAt: null,
    completedByName: null,
    completionMinutes: null,
    checklistItems: ['Linen sent as infected', 'All surfaces wiped', 'Curtains changed'],
    checklist: [],
    notes: null,
    verifiedAt: null,
    verifiedByName: null,
    verificationRemarks: null,
    completedByYou: false,
    version: 1,
  },
];

const REPORT: HousekeepingReport = {
  from: today,
  to: today,
  requests: [
    { kind: 'Spill', requests: 4, targetMinutes: 15, medianResponseMinutes: 12, late: 1, sentBack: 0 },
    { kind: 'Terminal', requests: 2, targetMinutes: 60, medianResponseMinutes: 40, late: 0, sentBack: 1 },
  ],
  scheduledDue: 12,
  scheduledDone: 10,
  scheduledVerified: 8,
  sentBack: 1,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/housekeeping/areas': AREAS,
  'GET /api/v1/housekeeping/board': BOARD,
  'GET /api/v1/housekeeping/requests': REQUESTS,
  'GET /api/v1/housekeeping/report': REPORT,
};
