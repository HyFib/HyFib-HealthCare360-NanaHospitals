import type {
  AssessmentDto,
  HandoverDto,
  IntakeOutputChart,
  MarEntryDto,
  MedicationOrderDto,
  NursingNoteDto,
  WardHandoverItem,
  WardMarItem,
} from '@/modules/inpatient/nursingApi';
import { type FakeAnswers, type FakeRequest } from '../fakeApi';
import { WARD_ID } from '../roleAccessIds';
import { IN_PATIENTS } from './inpatient';

// The nursing station on General Ward — Male (the in-patients of ./inpatient): the medication round (one dose
// overdue, two due within the hour), the shift's handover sheet (one handover to acknowledge, one acknowledged), and
// the chart of each patient: MAR, medicines, notes, handovers, intake/output and risk scores.

const now = Date.now();
const minutesFromNow = (minutes: number) => new Date(now + minutes * 60_000).toISOString();
const today = new Date(now).toISOString().slice(0, 10);
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000076${suffix}`;
const [SUNIL, RAVI, IMRAN] = [IN_PATIENTS[0]!, IN_PATIENTS[1]!, IN_PATIENTS[2]!];
const bed = (n: number) => `GWM-${String(n).padStart(2, '0')}`;

const due = (
  overrides: Partial<WardMarItem> & Pick<WardMarItem, 'entryId' | 'drugName' | 'dose' | 'scheduledAt'>,
): WardMarItem => ({
  admissionId: SUNIL.admissionId,
  patientId: SUNIL.patientId,
  uhid: SUNIL.uhid,
  patientName: SUNIL.patientName,
  bedLabel: bed(1),
  route: 'IV',
  isOverdue: false,
  minutesLate: 0,
  version: 1,
  ...overrides,
});

const MAR_DUE: WardMarItem[] = [
  due({
    entryId: id('01'),
    drugName: 'Inj. Ceftriaxone 1 g',
    dose: '1 g',
    scheduledAt: minutesFromNow(-40),
    isOverdue: true,
    minutesLate: 40,
  }),
  due({
    entryId: id('02'),
    drugName: 'Tab. Paracetamol 650 mg',
    dose: '1 tab',
    route: 'Oral',
    scheduledAt: minutesFromNow(20),
  }),
  due({
    entryId: id('03'),
    admissionId: RAVI.admissionId,
    patientId: RAVI.patientId,
    uhid: RAVI.uhid,
    patientName: RAVI.patientName,
    bedLabel: bed(2),
    drugName: 'Inj. Human Actrapid',
    dose: '8 units',
    route: 'SC',
    scheduledAt: minutesFromNow(35),
  }),
];

const handover = (
  overrides: Partial<HandoverDto> & Pick<HandoverDto, 'id' | 'admissionId' | 'patientId'>,
): HandoverDto => ({
  wardId: WARD_ID,
  dutyDate: today,
  fromShift: 'Night',
  situation: 'Stable overnight; afebrile since 2 am.',
  background: 'Day 3 of IV ceftriaxone for pneumonia.',
  assessment: 'SpO2 95% on room air, chest clearer.',
  recommendation: 'Repeat chest X-ray today; encourage spirometry.',
  pendingTasks: 'Sputum culture report awaited.',
  handedOverAt: minutesFromNow(-90),
  handedOverByName: 'Sr. Lalitha Menon',
  acknowledgedAt: null,
  acknowledgedByName: null,
  ...overrides,
});

const WARD_HANDOVERS: WardHandoverItem[] = [
  {
    handover: handover({ id: id('11'), admissionId: SUNIL.admissionId, patientId: SUNIL.patientId }),
    uhid: SUNIL.uhid,
    patientName: SUNIL.patientName,
    bedLabel: bed(1),
  },
  {
    handover: handover({
      id: id('12'),
      admissionId: IMRAN.admissionId,
      patientId: IMRAN.patientId,
      situation: 'Discharge initiated; awaiting billing clearance.',
      background: 'Acute pancreatitis, now tolerating soft diet.',
      recommendation: 'Explain discharge medicines to the attendant.',
      pendingTasks: null,
      acknowledgedAt: minutesFromNow(-60),
      acknowledgedByName: 'Sr. Kavita Pawar',
    }),
    uhid: IMRAN.uhid,
    patientName: IMRAN.patientName,
    bedLabel: bed(3),
  },
];

const MAR: MarEntryDto[] = [
  {
    id: id('01'),
    medicationOrderId: id('21'),
    drugName: 'Inj. Ceftriaxone 1 g',
    dose: '1 g',
    route: 'IV',
    scheduledAt: minutesFromNow(-40),
    status: 'Due',
    isOverdue: true,
    isAdHoc: false,
    administeredAt: null,
    recordedByName: null,
    reason: null,
    batchNumber: null,
    witnessName: null,
    notes: null,
    version: 1,
  },
  {
    id: id('04'),
    medicationOrderId: id('22'),
    drugName: 'Tab. Paracetamol 650 mg',
    dose: '1 tab',
    route: 'Oral',
    scheduledAt: minutesFromNow(-360),
    status: 'Given',
    isOverdue: false,
    isAdHoc: false,
    administeredAt: minutesFromNow(-355),
    recordedByName: 'Sr. Lalitha Menon',
    reason: null,
    batchNumber: 'PCM2409',
    witnessName: null,
    notes: null,
    version: 2,
  },
];

const order = (
  overrides: Partial<MedicationOrderDto> & Pick<MedicationOrderDto, 'id' | 'drugName' | 'dose' | 'frequency'>,
): MedicationOrderDto => ({
  prescriptionId: id('31'),
  drugItemId: null,
  genericName: null,
  strength: null,
  route: 'IV',
  durationDays: 5,
  startAt: minutesFromNow(-60 * 24 * 3),
  endAt: null,
  instructions: null,
  isControlledDrug: false,
  isAsNeeded: false,
  source: 'Prescription',
  status: 'Active',
  lastDispensedAt: minutesFromNow(-60 * 20),
  stoppedAt: null,
  stopReason: null,
  roundTimes: ['08:00:00', '20:00:00'],
  ...overrides,
});

const ORDERS: MedicationOrderDto[] = [
  order({
    id: id('21'),
    drugName: 'Inj. Ceftriaxone',
    genericName: 'Ceftriaxone',
    strength: '1 g',
    dose: '1 g',
    frequency: 'BD',
  }),
  order({
    id: id('22'),
    drugName: 'Tab. Paracetamol',
    genericName: 'Paracetamol',
    strength: '650 mg',
    dose: '1 tab',
    route: 'Oral',
    frequency: 'SOS',
    isAsNeeded: true,
    roundTimes: [],
    instructions: 'If temperature above 100 °F',
  }),
  order({
    id: id('23'),
    drugName: 'Inj. Pantoprazole',
    strength: '40 mg',
    dose: '40 mg',
    frequency: 'OD',
    status: 'Stopped',
    stoppedAt: minutesFromNow(-60 * 24),
    stopReason: 'Switched to oral',
    roundTimes: ['08:00:00'],
  }),
];

const NOTES: NursingNoteDto[] = [
  {
    id: id('41'),
    dutyDate: today,
    shift: 'Morning',
    category: 'Observation',
    note: 'Patient comfortable, took breakfast. Ambulated to the washroom with support.',
    noteFor: minutesFromNow(-50),
    recordedAt: minutesFromNow(-45),
    recordedByName: 'Sr. Kavita Pawar',
    isLateEntry: false,
  },
];

const HANDOVERS: HandoverDto[] = [WARD_HANDOVERS[0]!.handover];

const IO: IntakeOutputChart = {
  dutyDate: today,
  balance: {
    from: `${today}T08:00:00+05:30`,
    to: minutesFromNow(0),
    total: { intakeMl: 1450, outputMl: 900, balanceMl: 550 },
    byShift: [{ shift: 'Morning', intakeMl: 1450, outputMl: 900, balanceMl: 550 }],
    byRoute: [
      { route: 'Oral', direction: 'Intake', volumeMl: 450 },
      { route: 'IntravenousFluid', direction: 'Intake', volumeMl: 1000 },
      { route: 'Urine', direction: 'Output', volumeMl: 900 },
    ],
  },
  entries: [
    {
      id: id('51'),
      recordedAt: minutesFromNow(-120),
      shift: 'Morning',
      direction: 'Intake',
      route: 'IntravenousFluid',
      volumeMl: 1000,
      description: 'Ringer lactate',
      notes: null,
      recordedByName: 'Sr. Kavita Pawar',
    },
    {
      id: id('52'),
      recordedAt: minutesFromNow(-60),
      shift: 'Morning',
      direction: 'Output',
      route: 'Urine',
      volumeMl: 900,
      description: null,
      notes: null,
      recordedByName: 'Sr. Kavita Pawar',
    },
  ],
};

const ASSESSMENTS: AssessmentDto[] = [
  {
    id: id('61'),
    type: 'MorseFall',
    score: 55,
    risk: 'High',
    interpretation: 'High fall risk',
    responses: null,
    assessedAt: minutesFromNow(-60 * 6),
    assessedByName: 'Sr. Lalitha Menon',
    notes: 'Bed rails up; call bell in reach.',
  },
  {
    id: id('62'),
    type: 'Pain',
    score: 3,
    risk: 'Low',
    interpretation: 'Mild pain',
    responses: null,
    assessedAt: minutesFromNow(-90),
    assessedByName: 'Sr. Kavita Pawar',
    notes: null,
  },
];

/** Each part of an in-patient's nursing chart, by the last segment of its path. */
const CHART: Record<string, unknown> = {
  mar: MAR,
  'medication-orders': ORDERS,
  notes: NOTES,
  handovers: HANDOVERS,
  'intake-output': IO,
  assessments: ASSESSMENTS,
};

export const ANSWERS: FakeAnswers = {
  [`GET /api/v1/nursing/wards/${WARD_ID}/mar/due`]: MAR_DUE,
  [`GET /api/v1/nursing/wards/${WARD_ID}/handovers`]: WARD_HANDOVERS,
  // Every in-patient's chart reads the same (the station opens whichever bed was chosen last).
  'GET /api/v1/nursing/admissions/*': (request: FakeRequest) => CHART[request.path.split('/').at(-1) ?? ''],
};
