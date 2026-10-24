import { todayIso } from '@hc360/core';
import type {
  DialysisBoard,
  DialysisMachineDto,
  DialysisProfileDto,
  DialysisSessionDetail,
  DialysisSessionListItem,
  DialyzerDto,
} from '@/modules/dialysis/api';
import { paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The dialysis unit for today: machines (one due for disinfection), sessions on and off machines, a patient due but not
// booked, and one enrolled patient and one booked session as records.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const DAY = 24 * 60;
const today = todayIso();

const machine = (
  n: string,
  code: string,
  station: string,
  status: DialysisMachineDto['status'],
  extra: Partial<DialysisMachineDto> = {},
): DialysisMachineDto => ({
  id: id(n),
  code,
  model: '4008S',
  manufacturer: 'Fresenius',
  serialNumber: `FMC-${code}`,
  station,
  seroDedication: 'Negative',
  status,
  lastDisinfectedAt: minutesAgo(5 * 60),
  lastDisinfectionMethod: 'Heat disinfection',
  lastUsedAt: minutesAgo(6 * 60),
  needsDisinfection: false,
  isActive: true,
  version: 4,
  ...extra,
});

const MACHINES: DialysisMachineDto[] = [
  machine('dd01', 'HD-01', 'Bay 1', 'Available'),
  machine('dd02', 'HD-02', 'Bay 2', 'Available', {
    needsDisinfection: true,
    lastDisinfectedAt: minutesAgo(DAY),
    lastUsedAt: minutesAgo(60),
  }),
  machine('dd03', 'HD-03', 'Bay 3', 'InUse'),
  machine('dd04', 'HD-04', 'Isolation bay', 'Available', { seroDedication: 'HBsAg' }),
  machine('dd05', 'HD-05', 'Bay 5', 'Maintenance', { lastUsedAt: minutesAgo(9 * DAY) }),
];

const SUNIL = { patientId: RECORD_ID, uhid: 'NH26000187', patientName: 'Sunil Vasant Deshmukh' };
const SAVITA = { patientId: id('d0a2'), uhid: 'NH26000317', patientName: 'Savita Bhosale' };
const ANWAR = { patientId: id('d0a3'), uhid: 'NH26000322', patientName: 'Anwar Hussain' };

const session = (
  n: string,
  sessionNumber: string,
  who: typeof SUNIL,
  status: DialysisSessionListItem['status'],
  machineId: string | null,
  machineCode: string | null,
  extra: Partial<DialysisSessionListItem> = {},
): DialysisSessionListItem => ({
  id: n === 'record' ? RECORD_ID : id(n),
  sessionNumber,
  ...who,
  scheduledDate: today,
  shift: 'Morning',
  status,
  machineId,
  machineCode,
  startedAt: null,
  endedAt: null,
  chargeStatus: 'NotDue',
  version: 2,
  ...extra,
});

const SESSIONS: DialysisSessionListItem[] = [
  session('record', 'DS26-01412', SUNIL, 'Scheduled', id('dd01'), 'HD-01'),
  session('d5e2', 'DS26-01413', SAVITA, 'InProgress', id('dd03'), 'HD-03', { startedAt: minutesAgo(95) }),
  session('d5e3', 'DS26-01409', ANWAR, 'Completed', id('dd04'), 'HD-04', {
    shift: 'Night',
    scheduledDate: todayIso(-1),
    startedAt: minutesAgo(DAY),
    endedAt: minutesAgo(DAY - 240),
    chargeStatus: 'Posted',
  }),
];

const UNASSIGNED: DialysisSessionListItem[] = [
  session('d5e4', 'DS26-01415', SAVITA, 'Scheduled', null, null, { shift: 'Afternoon' }),
];

const BOARD: DialysisBoard = {
  date: today,
  shift: null,
  machines: MACHINES.filter((m) => m.status !== 'Retired').map((m) => ({
    id: m.id,
    code: m.code,
    station: m.station,
    seroDedication: m.seroDedication,
    status: m.status,
    needsDisinfection: m.needsDisinfection,
    sessions: SESSIONS.filter((s) => s.machineId === m.id && s.scheduledDate === today),
  })),
  unassigned: UNASSIGNED,
  dueNotScheduled: [
    {
      patientId: ANWAR.patientId,
      uhid: ANWAR.uhid,
      patientName: ANWAR.patientName,
      seroStatus: 'HBsAg',
      shift: 'Evening',
      seroRescreeningDue: true,
    },
  ],
};

const profile = (
  who: typeof SUNIL,
  gender: DialysisProfileDto['gender'],
  age: string,
  extra: Partial<DialysisProfileDto> = {},
): DialysisProfileDto => ({
  id: id(`d9${who.uhid.slice(-2)}`),
  ...who,
  gender,
  age,
  dryWeightKg: 64.5,
  vascularAccess: 'AVF',
  accessSite: 'Left radio-cephalic',
  accessCreatedOn: '2025-11-12',
  scheduleDays: ['Monday', 'Wednesday', 'Friday', 'Saturday'],
  shift: 'Morning',
  sessionMinutes: 240,
  seroStatus: 'Negative',
  seroTestedOn: todayIso(-60),
  seroRescreeningDue: false,
  reusePolicy: 'Reuse',
  maxDialyzerUses: 8,
  effectiveMaxDialyzerUses: 8,
  nephrologistId: '0192a000-0000-7000-8000-0000000000d1',
  nephrologistName: 'Dr. Meera Iyer',
  primaryDiagnosis: 'CKD stage 5 — diabetic nephropathy',
  anticoagulationNotes: 'Heparin 2000 U loading, 500 U/h; stop 1 h before end.',
  enrolledOn: '2025-12-01',
  isActive: true,
  version: 3,
  ...extra,
});

const PROFILES: DialysisProfileDto[] = [
  profile(SUNIL, 'Male', '55Y'),
  profile(SAVITA, 'Female', '46Y', { vascularAccess: 'Catheter', accessSite: 'Right internal jugular' }),
  profile(ANWAR, 'Male', '63Y', {
    seroStatus: 'HBsAg',
    seroRescreeningDue: true,
    reusePolicy: 'SingleUse',
    maxDialyzerUses: 1,
    effectiveMaxDialyzerUses: 1,
    shift: 'Evening',
  }),
];

const DIALYZERS: DialyzerDto[] = [
  {
    id: id('d1a1'),
    patientId: RECORD_ID,
    serialNumber: 'FX80-2611-0457',
    brand: 'Fresenius',
    model: 'FX 80',
    surfaceAreaM2: 1.8,
    usesCount: 3,
    maxUses: 8,
    remainingUses: 5,
    firstUsedAt: minutesAgo(7 * DAY),
    lastUsedAt: minutesAgo(2 * DAY),
    isDiscarded: false,
    discardedAt: null,
    discardReason: null,
  },
];

const SESSION: DialysisSessionDetail = {
  id: RECORD_ID,
  sessionNumber: 'DS26-01412',
  ...SUNIL,
  seroStatus: 'Negative',
  dryWeightKg: 64.5,
  visitId: id('d7b1'),
  admissionId: null,
  scheduledDate: today,
  shift: 'Morning',
  status: 'Scheduled',
  machineId: id('dd01'),
  machineCode: 'HD-01',
  preWeightKg: 67.2,
  preSystolic: 152,
  preDiastolic: 88,
  prePulse: 84,
  preTemperatureC: 36.7,
  targetUfMl: 2500,
  preAssessedAt: minutesAgo(10),
  readings: [],
  postWeightKg: null,
  postSystolic: null,
  postDiastolic: null,
  postPulse: null,
  ufAchievedMl: null,
  heparinLoadingUnits: null,
  heparinHourlyUnits: null,
  dialyzerId: null,
  dialyzerSerial: null,
  dialyzerUseNumber: null,
  dialyzerMaxUses: null,
  complications: [],
  complicationNotes: null,
  startedAt: null,
  endedAt: null,
  durationMinutes: null,
  performedByName: null,
  cancelReason: null,
  chargeStatus: 'NotDue',
  chargeError: null,
  version: 2,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/dialysis/machines': MACHINES,
  'GET /api/v1/dialysis/board': BOARD,
  'GET /api/v1/dialysis/sessions': paged(SESSIONS),
  [`GET /api/v1/dialysis/sessions/${RECORD_ID}`]: SESSION,
  'GET /api/v1/dialysis/patients': paged(PROFILES),
  ...Object.fromEntries(PROFILES.map((p) => [`GET /api/v1/dialysis/patients/${p.patientId}`, p])),
  // Each patient's own dialyzers (/patients/{id}/dialyzers); a patient who is not enrolled stays a 404.
  'GET /api/v1/dialysis/patients/*': (request: FakeRequest) =>
    request.path.endsWith('/dialyzers') ? DIALYZERS : undefined,
};
