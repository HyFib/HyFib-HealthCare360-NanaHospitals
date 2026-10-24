import type {
  CensusReport,
  CensusSummary,
  AdmissionDetail,
  AdmissionListItem,
  AvailableBedDto,
  BedAssignmentDto,
  BedBoardItem,
  BedBoardWard,
  BedCounts,
  BedDto,
  BedHistoryItem,
  BedOccupant,
  ConsultantVisitDto,
  DailyChargeDto,
  DischargeClearanceDto,
  HousekeepingTaskDto,
  OccupancyReport,
  PatientAdmissionItem,
  PendingDischargeItem,
  RoomDto,
  WristbandDto,
} from '@/modules/inpatient/api';
import type { BedStatus } from '@/modules/inpatient/bedState';
import { paged, STAFF_USER, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID, WARD_ID } from '../roleAccessIds';

// The in-patient screens on General Ward — Male (WARD_ID, 12 beds): a bed in every state, so the bed board offers
// each bed action; the admissions in hospital (RECORD_ID is Sunil Deshmukh's, still admitted, so transfer and discharge
// are offered); one discharge waiting for billing clearance; housekeeping tasks waiting and in progress.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000075${suffix}`;
const WARD = { id: WARD_ID, code: 'GWM', name: 'General Ward — Male' };
const ROOM_ID = id('20');
const DEPARTMENT = { id: '0192a000-0000-7000-8000-00000000de01', name: 'General medicine' };
const bedId = (n: number) => id(`b${n}`);

export interface InPatient {
  admissionId: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  age: string;
  bed: number;
  days: number;
  diagnosis: string;
}

/** Who is on the ward (all men: the ward is male only); the first is the admission opened as a record. */
export const IN_PATIENTS: InPatient[] = [
  {
    admissionId: RECORD_ID,
    admissionNumber: 'IP2600231',
    patientId: RECORD_ID,
    uhid: 'NH26000187',
    patientName: 'Sunil Vasant Deshmukh',
    age: '55Y',
    bed: 1,
    days: 3,
    diagnosis: 'Community-acquired pneumonia',
  },
  {
    admissionId: id('a2'),
    admissionNumber: 'IP2600224',
    patientId: '0192a000-0000-7000-8000-000000000002',
    uhid: 'NH26000124',
    patientName: 'Ravi Shankar',
    age: '62Y',
    bed: 2,
    days: 6,
    diagnosis: 'Type 2 diabetes with foot ulcer',
  },
  {
    admissionId: id('a3'),
    admissionNumber: 'IP2600219',
    patientId: id('21'),
    uhid: 'NH26000152',
    patientName: 'Imran Qureshi',
    age: '47Y',
    bed: 3,
    days: 8,
    diagnosis: 'Acute pancreatitis',
  },
];

const bedLabel = (n: number) => `GWM-${String(n).padStart(2, '0')}`;

const occupantOf = (p: InPatient, status: BedOccupant['status'] = 'Admitted'): BedOccupant => ({
  admissionId: p.admissionId,
  admissionNumber: p.admissionNumber,
  patientId: p.patientId,
  uhid: p.uhid,
  patientName: p.patientName,
  gender: 'Male',
  age: p.age,
  consultantName: 'Dr. Meera Iyer',
  admittedAt: minutesAgo(60 * 24 * p.days),
  lengthOfStayDays: p.days,
  expectedDischargeDate: dateIn(status === 'DischargeInitiated' ? 0 : 2),
  status,
  payerType: p.bed === 2 ? 'Staff' : 'Self',
});

const tile = (n: number, status: BedStatus, overrides: Partial<BedBoardItem> = {}): BedBoardItem => ({
  bedId: bedId(n),
  label: bedLabel(n),
  roomNumber: '101',
  status,
  statusReason: null,
  statusChangedAt: minutesAgo(90),
  hasOxygen: n <= 4,
  hasVentilator: false,
  hasMonitor: n === 1,
  reservedUntil: null,
  occupant: null,
  alerts: [],
  ...overrides,
});

const BOARD_BEDS: BedBoardItem[] = [
  tile(1, 'Occupied', { occupant: occupantOf(IN_PATIENTS[0]!), alerts: ['HighFallRisk', 'MedicationOverdue'] }),
  tile(2, 'Occupied', { occupant: occupantOf(IN_PATIENTS[1]!), alerts: ['PressureInjuryRisk'] }),
  tile(3, 'Occupied', {
    occupant: occupantOf(IN_PATIENTS[2]!, 'DischargeInitiated'),
    alerts: ['DischargeInitiated', 'DischargeDueToday'],
  }),
  tile(4, 'Vacant'),
  tile(5, 'Reserved', { statusReason: 'Elective admission from OPD', reservedUntil: minutesAgo(-180) }),
  tile(6, 'Cleaning', { statusReason: 'After discharge', alerts: ['CleaningDelayed'] }),
  tile(7, 'Cleaning', { statusReason: 'After transfer' }),
  tile(8, 'Blocked', { statusReason: 'Isolation of the next bed' }),
  tile(9, 'Maintenance', { statusReason: 'Oxygen outlet leaking' }),
];

const counts = (beds: { status: BedStatus }[], total = 12): BedCounts => {
  const of = (status: BedStatus) => beds.filter((b) => b.status === status).length;
  const vacant = of('Vacant') + (total - beds.length);
  const operational = total - of('Blocked') - of('Maintenance');
  return {
    total,
    occupied: of('Occupied'),
    vacant,
    reserved: of('Reserved'),
    cleaning: of('Cleaning'),
    blocked: of('Blocked'),
    maintenance: of('Maintenance'),
    operational,
    occupancyPercent: Math.round((of('Occupied') / operational) * 100),
  };
};

const BOARD: BedBoardWard[] = [
  {
    wardId: WARD.id,
    code: WARD.code,
    name: WARD.name,
    type: 'General',
    floor: '1',
    nurseStation: 'NS-1',
    counts: counts(BOARD_BEDS),
    beds: BOARD_BEDS,
  },
];

const OCCUPANCY: OccupancyReport = {
  asOf: minutesAgo(1),
  total: counts(BOARD_BEDS),
  wards: [{ wardId: WARD.id, code: WARD.code, name: WARD.name, type: 'General', counts: counts(BOARD_BEDS) }],
  byType: [{ type: 'General', counts: counts(BOARD_BEDS) }],
};

const ROOMS: RoomDto[] = [
  {
    id: ROOM_ID,
    wardId: WARD.id,
    number: '101',
    description: 'Twelve-bed bay',
    isIsolation: false,
    isActive: true,
    beds: 12,
    version: 1,
  },
  {
    id: id('22'),
    wardId: WARD.id,
    number: '102',
    description: 'Isolation room',
    isIsolation: true,
    isActive: false,
    beds: 0,
    version: 2,
  },
];

const BEDS: BedDto[] = BOARD_BEDS.map((b, index) => ({
  id: b.bedId,
  wardId: WARD.id,
  wardName: WARD.name,
  roomId: ROOM_ID,
  roomNumber: '101',
  label: b.label,
  status: b.status,
  statusReason: b.statusReason,
  statusChangedAt: b.statusChangedAt,
  currentAdmissionId: b.occupant?.admissionId ?? null,
  reservedForPatientId: b.status === 'Reserved' ? id('23') : null,
  reservedUntil: b.reservedUntil,
  hasOxygen: b.hasOxygen,
  hasVentilator: b.hasVentilator,
  hasMonitor: b.hasMonitor,
  displayOrder: index,
  isActive: true,
  nextStatuses: b.status === 'Vacant' ? ['Occupied', 'Reserved', 'Cleaning', 'Blocked', 'Maintenance'] : [],
  version: 1,
}));

const AVAILABLE: AvailableBedDto[] = [4, 10, 11].map((n) => ({
  bedId: bedId(n),
  label: bedLabel(n),
  wardId: WARD.id,
  wardName: WARD.name,
  wardType: 'General',
  roomNumber: '101',
  status: 'Vacant',
  hasOxygen: n === 4,
  hasVentilator: false,
  hasMonitor: false,
  isEligible: true,
  ineligibleReason: null,
}));

const HOUSEKEEPING: HousekeepingTaskDto[] = [
  {
    id: id('24'),
    bedId: bedId(6),
    bedLabel: bedLabel(6),
    wardId: WARD.id,
    wardName: WARD.name,
    admissionId: id('a0'),
    trigger: 'Discharge',
    status: 'Pending',
    requestedAt: minutesAgo(75),
    startedAt: null,
    completedAt: null,
    turnaroundMinutes: null,
    waitingMinutes: 75,
    notes: null,
  },
  {
    id: id('25'),
    bedId: bedId(7),
    bedLabel: bedLabel(7),
    wardId: WARD.id,
    wardName: WARD.name,
    admissionId: null,
    trigger: 'Transfer',
    status: 'InProgress',
    requestedAt: minutesAgo(40),
    startedAt: minutesAgo(15),
    completedAt: null,
    turnaroundMinutes: null,
    waitingMinutes: 40,
    notes: 'Mattress to be changed',
  },
];

const BED_HISTORY: BedHistoryItem[] = [
  {
    admissionId: RECORD_ID,
    admissionNumber: 'IP2600231',
    patientId: RECORD_ID,
    uhid: 'NH26000187',
    patientName: 'Sunil Vasant Deshmukh',
    reason: 'Admission',
    fromAt: minutesAgo(60 * 24 * 3),
    toAt: null,
  },
];

const admission = (p: InPatient, status: AdmissionListItem['status'] = 'Admitted'): AdmissionListItem => ({
  id: p.admissionId,
  admissionNumber: p.admissionNumber,
  patientId: p.patientId,
  uhid: p.uhid,
  patientName: p.patientName,
  gender: 'Male',
  age: p.age,
  status,
  type: p.bed === 3 ? 'Emergency' : 'Elective',
  admittedAt: minutesAgo(60 * 24 * p.days),
  dischargedAt: null,
  wardId: WARD.id,
  wardName: WARD.name,
  bedId: bedId(p.bed),
  bedLabel: bedLabel(p.bed),
  consultantPractitionerId: STAFF_USER.practitionerId,
  consultantName: 'Dr. Meera Iyer',
  lengthOfStayDays: p.days,
  payerType: p.bed === 2 ? 'Staff' : 'Self',
  isMlc: false,
});

const ADMISSIONS: AdmissionListItem[] = [
  admission(IN_PATIENTS[0]!),
  admission(IN_PATIENTS[1]!),
  admission(IN_PATIENTS[2]!, 'DischargeInitiated'),
  {
    ...admission({ ...IN_PATIENTS[1]!, admissionId: id('a4'), admissionNumber: 'IP2600187', days: 4 }, 'Discharged'),
    admittedAt: minutesAgo(60 * 24 * 40),
    dischargedAt: minutesAgo(60 * 24 * 36),
    bedId: null,
    bedLabel: null,
  },
];

const ADMISSION: AdmissionDetail = {
  id: RECORD_ID,
  admissionNumber: 'IP2600231',
  patientId: RECORD_ID,
  uhid: 'NH26000187',
  patientName: 'Sunil Vasant Deshmukh',
  gender: 'Male',
  age: '55Y',
  visitId: id('26'),
  visitNumber: 'IP2600231',
  sourceVisitId: id('0f'),
  type: 'Elective',
  source: 'OutPatient',
  status: 'Admitted',
  admittedAt: minutesAgo(60 * 24 * 3),
  admittingPractitionerId: STAFF_USER.practitionerId,
  admittingPractitionerName: 'Dr. Meera Iyer',
  consultantPractitionerId: STAFF_USER.practitionerId,
  consultantName: 'Dr. Meera Iyer',
  departmentId: DEPARTMENT.id,
  departmentName: DEPARTMENT.name,
  wardId: WARD.id,
  wardName: WARD.name,
  bedId: bedId(1),
  bedLabel: bedLabel(1),
  provisionalDiagnosis: 'Community-acquired pneumonia',
  referredBy: null,
  payerType: 'Self',
  payerId: null,
  policyNumberMasked: null,
  isMlc: false,
  mlcNumber: null,
  expectedDischargeDate: dateIn(2),
  attendantName: 'Anjali Deshmukh',
  attendantRelation: 'Wife',
  attendantPhone: '+919822054322',
  notes: null,
  lengthOfStayDays: 3,
  dischargeInitiatedAt: null,
  dischargeType: null,
  dischargedAt: null,
  dischargeNotes: null,
  cancelledAt: null,
  cancelReason: null,
  version: 5,
};

const BED_ASSIGNMENTS: BedAssignmentDto[] = [
  {
    id: id('ba'),
    bedId: bedId(1),
    bedLabel: bedLabel(1),
    wardId: WARD.id,
    wardName: WARD.name,
    reason: 'Admission',
    transferReason: null,
    fromAt: minutesAgo(60 * 24 * 3),
    toAt: null,
  },
];

const CHARGES: DailyChargeDto[] = [0, 1, 2].flatMap((day) => [
  {
    chargeDate: dateIn(-day),
    kind: 'Bed' as const,
    serviceItemId: id('27'),
    serviceCode: 'BED-GEN',
    sourceReference: `${RECORD_ID}:${dateIn(-day)}:bed`,
    chargeId: id(`c${day}`),
    postedAt: minutesAgo(60 * 24 * day + 30),
    cancelledAt: null,
  },
]);

const CONSULTANT_VISITS: ConsultantVisitDto[] = [
  {
    id: id('28'),
    practitionerId: STAFF_USER.practitionerId!,
    practitionerName: 'Dr. Meera Iyer',
    visitedAt: minutesAgo(180),
    isCriticalCare: false,
    serviceCode: 'IPVISIT',
    notes: 'Afebrile since morning; continue IV antibiotics.',
    isCharged: true,
    chargeError: null,
  },
];

const CLEARANCE: DischargeClearanceDto = { admissionId: RECORD_ID, cleared: false, outstanding: 18_450, reason: null };

const WRISTBAND: WristbandDto = {
  admissionId: RECORD_ID,
  uhid: 'NH26000187',
  patientName: 'Sunil Vasant Deshmukh',
  gender: 'Male',
  age: '55Y',
  admissionNumber: 'IP2600231',
  wardName: WARD.name,
  bedLabel: bedLabel(1),
  allergies: 'NoKnownAllergies',
};

const PENDING_DISCHARGES: PendingDischargeItem[] = [
  {
    admissionId: id('a3'),
    admissionNumber: 'IP2600219',
    patientId: id('21'),
    uhid: 'NH26000152',
    patientName: 'Imran Qureshi',
    wardName: WARD.name,
    bedLabel: bedLabel(3),
    consultantName: 'Dr. Meera Iyer',
    dischargeInitiatedAt: minutesAgo(130),
    expectedDischargeDate: dateIn(0),
    waitingMinutes: 130,
  },
];

// The census (GET /api/v1/inpatient/census) is left unanswered on purpose: with it the screen shows the pending
// discharges table, and its "Pending discharges" tile then scrolls to it with scrollIntoView, which the test DOM lacks;
// the error thrown fails the whole check.

const PATIENT_ADMISSIONS: PatientAdmissionItem[] = [
  {
    id: RECORD_ID,
    admissionNumber: 'IP2600231',
    status: 'Admitted',
    type: 'Elective',
    admittedAt: minutesAgo(60 * 24 * 3),
    dischargedAt: null,
    wardId: WARD.id,
    wardName: WARD.name,
    consultantPractitionerId: STAFF_USER.practitionerId,
    consultantName: 'Dr. Meera Iyer',
    dischargeType: null,
    lengthOfStayDays: 3,
  },
];

const isoDay = (daysAgo: number) => new Date(Date.now() - daysAgo * 86_400_000).toISOString().slice(0, 10);

const CENSUS_SUMMARY: CensusSummary = {
  from: isoDay(6),
  to: isoDay(0),
  admissions: 9,
  discharges: 7,
  deaths: 0,
  inpatientDays: 58,
  averageLengthOfStay: 4.2,
  bedOccupancyRate: 69,
  days: [],
};

const CENSUS: CensusReport = {
  from: isoDay(6),
  to: isoDay(0),
  bedsInService: 12,
  hospital: CENSUS_SUMMARY,
  wards: [
    { wardId: WARD.id, code: WARD.code, name: WARD.name, type: 'General', bedsInService: 12, summary: CENSUS_SUMMARY },
  ],
  housekeepingTasksCompleted: 6,
  averageHousekeepingTurnaroundMinutes: 35,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/inpatient/census': CENSUS,
  'GET /api/v1/beds': BEDS,
  'GET /api/v1/beds/board': BOARD,
  'GET /api/v1/beds/occupancy': OCCUPANCY,
  'GET /api/v1/beds/available': AVAILABLE,
  'GET /api/v1/beds/housekeeping': HOUSEKEEPING,
  ...Object.fromEntries(BOARD_BEDS.map((b) => [`GET /api/v1/beds/${b.bedId}/history`, paged(BED_HISTORY)])),
  [`GET /api/v1/inpatient/wards/${WARD_ID}/rooms`]: ROOMS,
  // "In hospital now" (activeOnly) leaves out the discharged admission, as the server does.
  'GET /api/v1/inpatient/admissions': (request: FakeRequest) =>
    paged(
      request.url.searchParams.get('activeOnly') === 'true'
        ? ADMISSIONS.filter((a) => a.status === 'Admitted' || a.status === 'DischargeInitiated')
        : ADMISSIONS,
    ),
  [`GET /api/v1/inpatient/admissions/${RECORD_ID}`]: ADMISSION,
  [`GET /api/v1/inpatient/admissions/${RECORD_ID}/bed-history`]: BED_ASSIGNMENTS,
  [`GET /api/v1/inpatient/admissions/${RECORD_ID}/charges`]: CHARGES,
  [`GET /api/v1/inpatient/admissions/${RECORD_ID}/consultant-visits`]: CONSULTANT_VISITS,
  [`GET /api/v1/inpatient/admissions/${RECORD_ID}/discharge/clearance`]: CLEARANCE,
  [`GET /api/v1/inpatient/admissions/${RECORD_ID}/wristband`]: WRISTBAND,
  'GET /api/v1/inpatient/discharges/pending': PENDING_DISCHARGES,
  [`GET /api/v1/patients/${RECORD_ID}/admissions`]: PATIENT_ADMISSIONS,
};
