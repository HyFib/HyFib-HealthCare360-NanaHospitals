import type * as Core from '@hc360/core';
import type { Letterhead } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PatientFlags } from '@/modules/emr/api';
import type {
  AdmissionDetail,
  BedBoardItem,
  BedBoardWard,
  BedCounts,
  BedOccupant,
  WardDto,
} from '@/modules/inpatient/api';
import { countBeds, occupancyPercent } from '@/modules/inpatient/bedState';
import type { MarEntryDto, MedicationOrderDto, WardHandoverItem, WardMarItem } from '@/modules/inpatient/nursingApi';
import { istDate } from '@/modules/inpatient/time';
import {
  fakeServer,
  openScreen,
  SCREEN_TESTS,
  SLOW,
  STAFF_USER,
  stubBrowserApis,
  type FakeRequest,
} from '../appScreen';

// The wards' screens, whole (shell included) with canned data, checked with axe-core for WCAG 2.2 A and AA problems
// (SRS UX-10): the bed board, the nursing station and an admission. Colour contrast and target sizes are checked in a
// browser instead (FRONTEND-GUIDE.md).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const today = istDate(now);

const FEMALE_WARD = '0192a000-0000-7000-8000-000000000a01';
const ICU = '0192a000-0000-7000-8000-000000000a02';

const WARDS: WardDto[] = [
  {
    id: FEMALE_WARD,
    code: 'FMW',
    name: 'Female medical ward',
    type: 'General',
    floor: '2',
    block: 'A',
    genderPolicy: 'FemaleOnly',
    nurseStation: 'NS-2A',
    phone: '2201',
    departmentId: null,
    bedServiceItemId: null,
    bedServiceCode: 'BED-GEN',
    displayOrder: 1,
    isActive: true,
    notes: null,
    totalBeds: 7,
    occupiedBeds: 3,
    version: 1,
  },
  {
    id: ICU,
    code: 'MICU',
    name: 'Medical ICU',
    type: 'Icu',
    floor: '3',
    block: 'B',
    genderPolicy: 'Any',
    nurseStation: 'NS-3B',
    phone: '2301',
    departmentId: null,
    bedServiceItemId: null,
    bedServiceCode: 'BED-ICU',
    displayOrder: 2,
    isActive: true,
    notes: null,
    totalBeds: 5,
    occupiedBeds: 2,
    version: 1,
  },
];

const occupant = (
  overrides: Partial<BedOccupant> &
    Pick<BedOccupant, 'admissionId' | 'admissionNumber' | 'patientId' | 'uhid' | 'patientName'>,
): BedOccupant => ({
  gender: 'Female',
  age: '39Y',
  consultantName: 'Dr. Meera Iyer',
  admittedAt: minutesAgo(60 * 24 * 2),
  lengthOfStayDays: 3,
  expectedDischargeDate: null,
  status: 'Admitted',
  payerType: 'Self',
  ...overrides,
});

const ASHA = occupant({
  admissionId: '0192a000-0000-7000-8000-000000000c01',
  admissionNumber: 'IP26000045',
  patientId: '0192a000-0000-7000-8000-000000000001',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  payerType: 'Insurance',
  expectedDischargeDate: istDate(now + 2 * 24 * 3600_000),
});
const KAMALA = occupant({
  admissionId: '0192a000-0000-7000-8000-000000000c02',
  admissionNumber: 'IP26000041',
  patientId: '0192a000-0000-7000-8000-000000000002',
  uhid: 'NH26000127',
  patientName: 'Kamala Bai',
  age: '78Y',
  consultantName: 'Dr. Anil Rao',
  admittedAt: minutesAgo(60 * 24 * 5),
  lengthOfStayDays: 6,
  payerType: 'GovernmentScheme',
});
const SUNITA = occupant({
  admissionId: '0192a000-0000-7000-8000-000000000c03',
  admissionNumber: 'IP26000038',
  patientId: '0192a000-0000-7000-8000-000000000003',
  uhid: 'NH26000131',
  patientName: 'Sunita Pawar',
  age: '24Y',
  admittedAt: minutesAgo(60 * 24 * 7),
  lengthOfStayDays: 8,
  expectedDischargeDate: today,
  status: 'DischargeInitiated',
});
const RAVI = occupant({
  admissionId: '0192a000-0000-7000-8000-000000000c11',
  admissionNumber: 'IP26000047',
  patientId: '0192a000-0000-7000-8000-000000000004',
  uhid: 'NH26000124',
  patientName: 'Ravi Shankar',
  gender: 'Male',
  age: '62Y',
  admittedAt: minutesAgo(60 * 9),
  lengthOfStayDays: 1,
  payerType: 'Staff',
});
const SURESH = occupant({
  admissionId: '0192a000-0000-7000-8000-000000000c12',
  admissionNumber: 'IP26000046',
  patientId: '0192a000-0000-7000-8000-000000000005',
  uhid: 'NH26000135',
  patientName: 'Suresh Patil',
  gender: 'Male',
  age: null,
  consultantName: null,
  admittedAt: minutesAgo(60 * 30),
  lengthOfStayDays: 2,
  payerType: 'Corporate',
});

const bed = (overrides: Partial<BedBoardItem> & Pick<BedBoardItem, 'bedId' | 'label' | 'status'>): BedBoardItem => ({
  roomNumber: null,
  statusReason: null,
  statusChangedAt: minutesAgo(60 * 20),
  hasOxygen: false,
  hasVentilator: false,
  hasMonitor: false,
  reservedUntil: null,
  occupant: null,
  alerts: [],
  ...overrides,
});

/** Every state a bed can be in, with and without alerts (more than a tile shows, on FMW-02). */
const FEMALE_WARD_BEDS: BedBoardItem[] = [
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b01',
    label: 'FMW-01',
    status: 'Occupied',
    roomNumber: '201',
    hasOxygen: true,
    occupant: ASHA,
    alerts: ['MedicationOverdue'],
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b02',
    label: 'FMW-02',
    status: 'Occupied',
    roomNumber: '201',
    occupant: KAMALA,
    alerts: ['HighFallRisk', 'PressureInjuryRisk', 'SeverePain', 'MedicationOverdue'],
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b03',
    label: 'FMW-03',
    status: 'Occupied',
    roomNumber: '202',
    occupant: SUNITA,
    alerts: ['Mlc', 'DischargeInitiated', 'DischargeDueToday'],
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b04',
    label: 'FMW-04',
    status: 'Vacant',
    roomNumber: '202',
    hasOxygen: true,
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b05',
    label: 'FMW-05',
    status: 'Reserved',
    roomNumber: '203',
    statusReason: 'For a patient admitted from OPD',
    statusChangedAt: minutesAgo(30),
    reservedUntil: minutesAgo(-120),
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b06',
    label: 'FMW-06',
    status: 'Cleaning',
    roomNumber: '203',
    statusChangedAt: minutesAgo(75),
    alerts: ['CleaningDelayed'],
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b07',
    label: 'FMW-07',
    status: 'Blocked',
    roomNumber: '204',
    statusReason: 'Side rail broken; repair requested',
    statusChangedAt: minutesAgo(60 * 26),
  }),
];

const ICU_BEDS: BedBoardItem[] = [
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b11',
    label: 'MICU-01',
    status: 'Occupied',
    hasOxygen: true,
    hasVentilator: true,
    hasMonitor: true,
    occupant: RAVI,
    alerts: ['Restricted'],
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b12',
    label: 'MICU-02',
    status: 'Occupied',
    hasOxygen: true,
    hasMonitor: true,
    occupant: SURESH,
    alerts: ['Mlc'],
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b13',
    label: 'MICU-03',
    status: 'Maintenance',
    hasOxygen: true,
    hasVentilator: true,
    hasMonitor: true,
    statusReason: 'Ventilator annual service',
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b14',
    label: 'MICU-04',
    status: 'Reserved',
    hasOxygen: true,
    hasMonitor: true,
    statusChangedAt: minutesAgo(150),
    reservedUntil: minutesAgo(30),
    alerts: ['ReservationExpired'],
  }),
  bed({
    bedId: '0192a000-0000-7000-8000-000000000b15',
    label: 'MICU-05',
    status: 'Vacant',
    hasOxygen: true,
    hasMonitor: true,
  }),
];

/** Counts as the server works them out (BedCounts.From). */
function counts(beds: BedBoardItem[]): BedCounts {
  const byState = countBeds(beds.map((b) => b.status));
  return {
    ...byState,
    operational: byState.total - byState.blocked - byState.maintenance,
    occupancyPercent: occupancyPercent(byState),
  };
}

const BOARD: BedBoardWard[] = [
  {
    wardId: FEMALE_WARD,
    code: 'FMW',
    name: 'Female medical ward',
    type: 'General',
    floor: '2',
    nurseStation: 'NS-2A',
    counts: counts(FEMALE_WARD_BEDS),
    beds: FEMALE_WARD_BEDS,
  },
  {
    wardId: ICU,
    code: 'MICU',
    name: 'Medical ICU',
    type: 'Icu',
    floor: '3',
    nurseStation: 'NS-3B',
    counts: counts(ICU_BEDS),
    beds: ICU_BEDS,
  },
];

const dueDose = (
  patient: BedOccupant,
  bedLabel: string,
  overrides: Partial<WardMarItem> & Pick<WardMarItem, 'entryId' | 'drugName' | 'dose' | 'scheduledAt'>,
): WardMarItem => ({
  admissionId: patient.admissionId,
  patientId: patient.patientId,
  uhid: patient.uhid,
  patientName: patient.patientName,
  bedLabel,
  route: 'Oral',
  isOverdue: false,
  minutesLate: 0,
  version: 1,
  ...overrides,
});

/** The female ward's medication round: two doses overdue, two due within the hour. */
const DOSES_DUE: WardMarItem[] = [
  dueDose(ASHA, 'FMW-01', {
    entryId: '0192a000-0000-7000-8000-000000000e01',
    drugName: 'Inj. Ceftriaxone',
    dose: '1 g',
    route: 'IV',
    scheduledAt: minutesAgo(40),
    isOverdue: true,
    minutesLate: 40,
  }),
  dueDose(KAMALA, 'FMW-02', {
    entryId: '0192a000-0000-7000-8000-000000000e02',
    drugName: 'Tab. Paracetamol',
    dose: '650 mg',
    scheduledAt: minutesAgo(75),
    isOverdue: true,
    minutesLate: 75,
  }),
  dueDose(KAMALA, 'FMW-02', {
    entryId: '0192a000-0000-7000-8000-000000000e03',
    drugName: 'Inj. Enoxaparin',
    dose: '40 mg',
    route: 'SC',
    scheduledAt: minutesAgo(-20),
  }),
  dueDose(SUNITA, 'FMW-03', {
    entryId: '0192a000-0000-7000-8000-000000000e04',
    drugName: 'Tab. Pantoprazole',
    dose: '40 mg',
    scheduledAt: minutesAgo(-35),
  }),
];

/** The ward's handover sheet: one patient waiting for the receiving nurse, one already received. */
const HANDOVERS: WardHandoverItem[] = [
  {
    uhid: ASHA.uhid,
    patientName: ASHA.patientName,
    bedLabel: 'FMW-01',
    handover: {
      id: '0192a000-0000-7000-8000-000000000d01',
      admissionId: ASHA.admissionId,
      patientId: ASHA.patientId,
      wardId: FEMALE_WARD,
      dutyDate: today,
      fromShift: 'Night',
      situation: 'Fever up to 101.2 °F at 2 am; settled with paracetamol.',
      background: 'Community-acquired pneumonia, day 3 of IV ceftriaxone.',
      assessment: 'SpO₂ 95% on 2 L oxygen; chest clearer on the right.',
      recommendation: 'Repeat chest X-ray this morning; encourage oral fluids.',
      pendingTasks: 'Send the sputum culture sample',
      handedOverAt: minutesAgo(90),
      handedOverByName: 'Sr. Rekha Pillai',
      acknowledgedAt: null,
      acknowledgedByName: null,
    },
  },
  {
    uhid: KAMALA.uhid,
    patientName: KAMALA.patientName,
    bedLabel: 'FMW-02',
    handover: {
      id: '0192a000-0000-7000-8000-000000000d02',
      admissionId: KAMALA.admissionId,
      patientId: KAMALA.patientId,
      wardId: FEMALE_WARD,
      dutyDate: today,
      fromShift: 'Night',
      situation: 'Pain score 7 at 5 am in the left hip.',
      background: 'Fall at home; pressure-injury risk, turned two-hourly.',
      assessment: null,
      recommendation: 'Review pain relief on the consultant round.',
      pendingTasks: null,
      handedOverAt: minutesAgo(95),
      handedOverByName: 'Sr. Rekha Pillai',
      acknowledgedAt: minutesAgo(80),
      acknowledgedByName: 'Sr. Anjali Menon',
    },
  },
];

const order = (
  overrides: Partial<MedicationOrderDto> & Pick<MedicationOrderDto, 'id' | 'drugName' | 'dose' | 'frequency'>,
): MedicationOrderDto => ({
  prescriptionId: '0192a000-0000-7000-8000-000000000501',
  drugItemId: null,
  genericName: null,
  strength: null,
  route: 'Oral',
  durationDays: 5,
  startAt: minutesAgo(60 * 24 * 2),
  endAt: null,
  instructions: null,
  isControlledDrug: false,
  isAsNeeded: false,
  source: 'Prescription',
  status: 'Active',
  lastDispensedAt: minutesAgo(60 * 20),
  stoppedAt: null,
  stopReason: null,
  roundTimes: [],
  ...overrides,
});

const ORDERS: MedicationOrderDto[] = [
  order({
    id: '0192a000-0000-7000-8000-000000000601',
    drugName: 'Inj. Ceftriaxone',
    genericName: 'Ceftriaxone',
    strength: '1 g',
    dose: '1 g',
    route: 'IV',
    frequency: 'BD',
    instructions: 'In 100 ml normal saline over 30 minutes',
    roundTimes: ['08:00:00', '20:00:00'],
  }),
  order({
    id: '0192a000-0000-7000-8000-000000000602',
    drugName: 'Tab. Paracetamol',
    genericName: 'Paracetamol',
    strength: '650 mg',
    dose: '650 mg',
    frequency: 'TDS',
    roundTimes: ['08:00:00', '14:00:00', '20:00:00'],
  }),
  order({
    id: '0192a000-0000-7000-8000-000000000603',
    drugName: 'Tab. Pantoprazole',
    genericName: 'Pantoprazole',
    strength: '40 mg',
    dose: '40 mg',
    frequency: 'OD',
    instructions: 'Before breakfast',
    roundTimes: ['07:00:00'],
  }),
  order({
    id: '0192a000-0000-7000-8000-000000000604',
    drugName: 'Inj. Ondansetron',
    genericName: 'Ondansetron',
    strength: '4 mg',
    dose: '4 mg',
    route: 'IV',
    frequency: 'SOS',
    isAsNeeded: true,
    source: 'Manual',
    prescriptionId: null,
  }),
];

const marEntry = (
  overrides: Partial<MarEntryDto> & Pick<MarEntryDto, 'id' | 'drugName' | 'dose' | 'scheduledAt' | 'status'>,
): MarEntryDto => ({
  medicationOrderId: null,
  route: 'Oral',
  isOverdue: false,
  isAdHoc: false,
  administeredAt: null,
  recordedByName: null,
  reason: null,
  batchNumber: null,
  witnessName: null,
  notes: null,
  version: 1,
  ...overrides,
});

/** Asha Devi's MAR today: a dose held, one given, an SOS dose, one overdue and one still to come. */
const MAR: MarEntryDto[] = [
  marEntry({
    id: '0192a000-0000-7000-8000-000000000e11',
    medicationOrderId: ORDERS[2]!.id,
    drugName: 'Tab. Pantoprazole',
    dose: '40 mg',
    scheduledAt: minutesAgo(60 * 5),
    status: 'Held',
    administeredAt: minutesAgo(60 * 5 - 10),
    recordedByName: 'Sr. Rekha Pillai',
    reason: 'Nil by mouth for ultrasound abdomen',
  }),
  marEntry({
    id: '0192a000-0000-7000-8000-000000000e12',
    medicationOrderId: ORDERS[1]!.id,
    drugName: 'Tab. Paracetamol',
    dose: '650 mg',
    scheduledAt: minutesAgo(60 * 4),
    status: 'Given',
    administeredAt: minutesAgo(60 * 4 - 5),
    recordedByName: 'Sr. Anjali Menon',
    batchNumber: 'PCM24091',
  }),
  marEntry({
    id: '0192a000-0000-7000-8000-000000000e13',
    medicationOrderId: ORDERS[3]!.id,
    drugName: 'Inj. Ondansetron',
    dose: '4 mg',
    route: 'IV',
    scheduledAt: minutesAgo(60 * 2),
    status: 'Given',
    isAdHoc: true,
    administeredAt: minutesAgo(60 * 2),
    recordedByName: 'Sr. Anjali Menon',
    reason: 'Nausea after breakfast',
  }),
  marEntry({
    id: '0192a000-0000-7000-8000-000000000e01',
    medicationOrderId: ORDERS[0]!.id,
    drugName: 'Inj. Ceftriaxone',
    dose: '1 g',
    route: 'IV',
    scheduledAt: minutesAgo(40),
    status: 'Due',
    isOverdue: true,
  }),
  marEntry({
    id: '0192a000-0000-7000-8000-000000000e14',
    medicationOrderId: ORDERS[1]!.id,
    drugName: 'Tab. Paracetamol',
    dose: '650 mg',
    scheduledAt: minutesAgo(-60 * 2),
    status: 'Due',
  }),
];

/** Asha Devi's allergies and alerts, on the patient banner. */
const FLAGS: PatientFlags = {
  allergyStatus: 'Recorded',
  allergies: [
    {
      id: '0192a000-0000-7000-8000-000000000701',
      allergen: 'Sulfonamides',
      type: 'Drug',
      severity: 'Severe',
      reaction: 'Widespread rash',
    },
  ],
  alerts: [
    {
      id: '0192a000-0000-7000-8000-000000000711',
      type: 'InfectionControl',
      title: 'Droplet precautions',
      detail: 'Until sputum culture is back',
      severity: 'Warning',
      expiresOn: null,
    },
  ],
};

const ADMISSION: AdmissionDetail = {
  id: ASHA.admissionId,
  admissionNumber: ASHA.admissionNumber,
  patientId: ASHA.patientId,
  uhid: ASHA.uhid,
  patientName: ASHA.patientName,
  gender: 'Female',
  age: '39Y',
  visitId: '0192a000-0000-7000-8000-000000000801',
  visitNumber: 'IPV26000045',
  sourceVisitId: '0192a000-0000-7000-8000-000000000802',
  type: 'Emergency',
  source: 'Emergency',
  status: 'Admitted',
  admittedAt: ASHA.admittedAt,
  admittingPractitionerId: '0192a000-0000-7000-8000-0000000000d2',
  admittingPractitionerName: 'Dr. Anil Rao',
  consultantPractitionerId: STAFF_USER.practitionerId,
  consultantName: 'Dr. Meera Iyer',
  departmentId: '0192a000-0000-7000-8000-00000000de01',
  departmentName: 'General medicine',
  wardId: FEMALE_WARD,
  wardName: 'Female medical ward',
  bedId: FEMALE_WARD_BEDS[0]!.bedId,
  bedLabel: 'FMW-01',
  provisionalDiagnosis: 'Community-acquired pneumonia, right lower lobe',
  referredBy: null,
  payerType: 'Insurance',
  payerId: '0192a000-0000-7000-8000-000000000901',
  policyNumberMasked: 'XXXXXX4321',
  isMlc: false,
  mlcNumber: null,
  expectedDischargeDate: ASHA.expectedDischargeDate,
  attendantName: 'Ramesh Kumar',
  attendantRelation: 'Husband',
  attendantPhone: '9876543210',
  notes: 'Vegetarian diet; prefers Hindi.',
  lengthOfStayDays: 3,
  dischargeInitiatedAt: null,
  dischargeType: null,
  dischargedAt: null,
  dischargeNotes: null,
  cancelledAt: null,
  cancelReason: null,
  version: 4,
};

const LETTERHEAD: Letterhead = { name: 'Nana Hospitals, Pune' };

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer({
    'GET /api/v1/inpatient/wards': WARDS,
    // All wards on the bed board; the nursing station asks for its own ward.
    'GET /api/v1/beds/board': ({ url }: FakeRequest) => {
      const wardId = url.searchParams.get('wardId');
      return wardId ? BOARD.filter((ward) => ward.wardId === wardId) : BOARD;
    },
    [`GET /api/v1/nursing/wards/${FEMALE_WARD}/mar/due`]: DOSES_DUE,
    [`GET /api/v1/nursing/wards/${FEMALE_WARD}/handovers`]: HANDOVERS,
    [`GET /api/v1/nursing/admissions/${ASHA.admissionId}/mar`]: MAR,
    [`GET /api/v1/nursing/admissions/${ASHA.admissionId}/medication-orders`]: ORDERS,
    [`GET /api/v1/emr/patients/${ASHA.patientId}/flags`]: FLAGS,
    [`GET /api/v1/inpatient/admissions/${ASHA.admissionId}`]: ADMISSION,
    'GET /api/v1/print/letterhead': LETTERHEAD,
  });
});

afterEach(() => {
  // A screen whose request went unanswered would show an error where its content should be, and hide that from axe.
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('in-patient screens', SCREEN_TESTS, () => {
  it('bed board, every kind of bed in two wards', async () => {
    openScreen('/inpatient/beds');
    expect(await screen.findByText('Kamala Bai', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByRole('option', { name: 'Medical ICU' }, SLOW)).toBeInTheDocument();
    expect(screen.getAllByText(/^(FMW|MICU)-\d{2}$/)).toHaveLength(12);
    await expectNoAxeViolations();
  });

  it('nursing station, before and after choosing the ward', async () => {
    openScreen('/nursing');
    const ward = await screen.findByRole('combobox', { name: 'Ward' }, SLOW);
    expect(await screen.findByRole('option', { name: 'Female medical ward' }, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();

    await userEvent.selectOptions(ward, 'Female medical ward');
    expect(await screen.findByRole('button', { name: /^Bed FMW-02, Kamala Bai/ }, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Inj. Enoxaparin', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Pain score 7 at 5 am in the left hip.', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it("nursing station, a patient's medication chart", async () => {
    // The ward chosen earlier on this tablet.
    localStorage.setItem('hc360:nursing:ward', FEMALE_WARD);
    openScreen('/nursing');
    await userEvent.click(await screen.findByRole('button', { name: /^Bed FMW-01, Asha Devi/ }, SLOW));
    expect(await screen.findByText('Nausea after breakfast', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText(/Sulfonamides/, {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('admission details', async () => {
    openScreen(`/inpatient/admissions/${ASHA.admissionId}`);
    expect(await screen.findByText('Community-acquired pneumonia, right lower lobe', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText(/Sulfonamides/, {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
