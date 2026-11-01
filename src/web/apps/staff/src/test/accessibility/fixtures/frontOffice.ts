import type { AuthUser } from '@hc360/core';
import type { PipelineStage } from '@hc360/ui';
import type { BillListItem, DayEndReport } from '@/modules/billing/api';
import type { FrontOfficeDashboard, QueueEntry } from '@/modules/front-office/api';
import type { InHospitalPatient, JourneyDto, PatientJourneys } from '@/modules/front-office/journeys';
import type { WardDto } from '@/modules/inpatient/api';
import type { PatientListItem } from '@/modules/patients/api';
import type {
  AppointmentDaySummary,
  AppointmentListItem,
  CalendarAppointment,
  PractitionerAvailability,
  SlotDto,
} from '@/modules/scheduling/api';
import { FACILITY_ID, paged, STAFF_USER, type FakeAnswers } from '../../fakeApi';

// The front desk's screens with canned data: patient search and registration, the queue, appointments and bills; the
// appointment calendar; and the front desk's home page.

export const OPD = { id: '0192a000-0000-7000-8000-00000000de01', code: 'GM', name: 'General medicine' };
export const DOCTORS = [
  { id: STAFF_USER.practitionerId!, code: 'D001', name: 'Dr. Meera Iyer' },
  { id: '0192a000-0000-7000-8000-0000000000d2', code: 'D002', name: 'Dr. Anil Rao' },
];
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();

export const PATIENTS: PatientListItem[] = [
  {
    id: '0192a000-0000-7000-8000-000000000001',
    uhid: 'NH26000123',
    fullName: 'Asha Devi',
    gender: 'Female',
    dateOfBirth: '1987-03-14',
    age: '39Y',
    phoneMasked: '98******10',
    category: 'General',
    isVip: false,
    isDeceased: false,
    registeredAt: minutesAgo(60 * 24 * 40),
    lastVisitAt: minutesAgo(60 * 24 * 3),
  },
  {
    id: '0192a000-0000-7000-8000-000000000002',
    uhid: 'NH26000124',
    fullName: 'Ravi Shankar',
    gender: 'Male',
    dateOfBirth: null,
    age: '62Y',
    phoneMasked: '97******21',
    category: 'Staff',
    isVip: true,
    isDeceased: false,
    registeredAt: minutesAgo(60 * 24 * 400),
    lastVisitAt: null,
  },
  {
    id: '0192a000-0000-7000-8000-000000000003',
    uhid: 'NH26000125',
    fullName: 'Kamala Bai',
    gender: 'Female',
    dateOfBirth: '1940-01-01',
    age: '86Y',
    phoneMasked: null,
    category: 'SeniorCitizen',
    isVip: false,
    isDeceased: true,
    registeredAt: minutesAgo(60 * 24 * 900),
    lastVisitAt: minutesAgo(60 * 24 * 30),
  },
];

const entry = (overrides: Partial<QueueEntry> & Pick<QueueEntry, 'tokenId' | 'label' | 'status'>): QueueEntry => ({
  priority: 'Normal',
  visitId: `${overrides.tokenId}-visit`,
  patientId: PATIENTS[0]!.id,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
  practitionerId: DOCTORS[0]!.id,
  room: 'Room 3',
  issuedAt: minutesAgo(50),
  calledAt: null,
  waitingMinutes: 50,
  chiefComplaint: 'Fever for three days',
  isFollowUp: false,
  ...overrides,
});

export const QUEUE: QueueEntry[] = [
  entry({ tokenId: 't1', label: 'GM-012', status: 'InService', calledAt: minutesAgo(5) }),
  entry({ tokenId: 't2', label: 'GM-013', status: 'Waiting', priority: 'Emergency', patientName: 'Ravi Shankar' }),
  entry({
    tokenId: 't3',
    label: 'GM-014',
    status: 'Waiting',
    isFollowUp: true,
    waitingMinutes: 12,
    chiefComplaint: null,
  }),
  entry({ tokenId: 't4', label: 'GM-009', status: 'Skipped', waitingMinutes: 0 }),
];

const appointment = (
  overrides: Partial<AppointmentListItem> & Pick<AppointmentListItem, 'id' | 'number' | 'status'>,
): AppointmentListItem => ({
  patientId: PATIENTS[0]!.id,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
  phoneMasked: '98******10',
  practitionerId: DOCTORS[0]!.id,
  practitionerName: 'Dr. Meera Iyer',
  departmentId: OPD.id,
  departmentName: OPD.name,
  startsAt: minutesAgo(-30),
  endsAt: minutesAgo(-45),
  seatNumber: 1,
  channel: 'Phone',
  visitType: 'New',
  isTelehealth: false,
  paymentStatus: 'PayAtHospital',
  feeAmount: 500,
  reason: 'Knee pain',
  room: 'Room 3',
  visitId: null,
  bookedAt: minutesAgo(60 * 24),
  ...overrides,
});

export const APPOINTMENTS: AppointmentListItem[] = [
  appointment({ id: 'a1', number: 'AP2600101', status: 'Booked' }),
  appointment({ id: 'a2', number: 'AP2600102', status: 'CheckedIn', patientName: 'Ravi Shankar', visitId: 'v2' }),
  appointment({ id: 'a3', number: 'AP2600103', status: 'Confirmed', isTelehealth: true, channel: 'Portal' }),
  appointment({ id: 'a4', number: 'AP2600104', status: 'Cancelled', visitType: 'FollowUp' }),
];

export const SUMMARY: AppointmentDaySummary = {
  date: '2026-10-10',
  total: 4,
  booked: 1,
  confirmed: 1,
  checkedIn: 1,
  inConsultation: 0,
  completed: 0,
  cancelled: 1,
  noShow: 0,
  telehealth: 1,
  online: 1,
  practitioners: [
    {
      practitionerId: DOCTORS[0]!.id,
      practitionerName: 'Dr. Meera Iyer',
      departmentId: OPD.id,
      capacity: 24,
      booked: 4,
      checkedIn: 1,
      completed: 0,
      noShow: 0,
      cancelled: 1,
      freeSlots: 20,
    },
  ],
};

const bill = (overrides: Partial<BillListItem> & Pick<BillListItem, 'id' | 'status'>): BillListItem => ({
  billNumber: `OP26000${overrides.id}`,
  type: 'OutPatient',
  isFinalBill: true,
  patientId: PATIENTS[0]!.id,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  visitId: 'v1',
  admissionId: null,
  payerType: 'Self',
  payerId: null,
  payerName: null,
  netAmount: 1250,
  patientAmount: 1250,
  payerAmount: 0,
  paidAmount: 0,
  dueAmount: 1250,
  createdAt: minutesAgo(90),
  finalizedAt: minutesAgo(80),
  ...overrides,
});

export const BILLS: BillListItem[] = [
  bill({ id: '11', status: 'Paid', paidAmount: 1250, dueAmount: 0 }),
  bill({
    id: '12',
    status: 'PartiallyPaid',
    paidAmount: 500,
    dueAmount: 750,
    payerType: 'Insurance',
    payerName: 'Star Health',
  }),
  bill({ id: '13', status: 'Draft', billNumber: null, finalizedAt: null }),
  bill({ id: '14', status: 'Cancelled' }),
];

/** A stage reached `ago` minutes ago (null: not reached); the current one counts its minutes from then. */
const stage = (
  key: string,
  label: string,
  state: PipelineStage['state'],
  ago: number | null = null,
  detail: string | null = null,
): PipelineStage => ({
  key,
  label,
  state,
  at: ago === null ? null : minutesAgo(ago),
  minutesInStage: state === 'Current' && ago !== null ? ago : null,
  detail,
});

const journey = (
  overrides: Partial<JourneyDto> & Pick<JourneyDto, 'kind' | 'id' | 'number' | 'stages'>,
): JourneyDto => ({
  title: 'General medicine · Dr. Meera Iyer',
  status: 'Active',
  startedAt: overrides.stages[0]?.at ?? minutesAgo(60),
  current: overrides.stages.find((s) => s.state === 'Current') ?? null,
  ...overrides,
});

/** Asha Devi: checked in 50 minutes ago, paid at the desk, with the doctor for the last 20 minutes. */
export const ASHA_VISIT = journey({
  kind: 'Outpatient',
  id: '0192a000-0000-7000-8000-00000000a501',
  number: 'OP2600981',
  stages: [
    stage('checked-in', 'Checked in', 'Done', 50, 'Token GM-012'),
    stage('called', 'Called', 'Done', 25, 'Room 3'),
    stage('in-consultation', 'In consultation', 'Current', 20),
    stage('consultation-signed', 'Consultation signed', 'Pending'),
    stage('investigations-done', 'Investigations done', 'Pending'),
    stage('dispensed', 'Medicines dispensed', 'Pending'),
    stage('billed', 'Billed', 'Done', 47),
    stage('paid', 'Paid', 'Done', 46),
    stage('closed', 'Visit closed', 'Pending'),
  ],
});

export const WARD: WardDto = {
  id: '0192a000-0000-7000-8000-00000000a401',
  code: 'GWM',
  name: 'General Ward — Male',
  type: 'General',
  floor: '1',
  block: null,
  genderPolicy: 'MaleOnly',
  nurseStation: null,
  phone: null,
  departmentId: null,
  bedServiceItemId: null,
  bedServiceCode: null,
  displayOrder: 0,
  isActive: true,
  notes: null,
  totalBeds: 12,
  occupiedBeds: 7,
  version: 1,
};

/** The board: Asha with the doctor, Ravi (restricted) waiting since check-in, a man in the general ward for two days. */
export const IN_HOSPITAL: InHospitalPatient[] = [
  {
    patientId: PATIENTS[0]!.id,
    uhid: PATIENTS[0]!.uhid,
    patientName: PATIENTS[0]!.fullName,
    isRestricted: false,
    gender: 'Female',
    age: '39Y',
    kind: 'Outpatient',
    departmentId: OPD.id,
    departmentName: OPD.name,
    wardId: null,
    place: 'Token GM-012',
    arrivedAt: minutesAgo(50),
    journey: ASHA_VISIT,
  },
  {
    patientId: PATIENTS[1]!.id,
    uhid: PATIENTS[1]!.uhid,
    patientName: 'R. S. (restricted)',
    isRestricted: true,
    gender: 'Male',
    age: '62Y',
    kind: 'Outpatient',
    departmentId: OPD.id,
    departmentName: OPD.name,
    wardId: null,
    place: 'Token GM-015',
    arrivedAt: minutesAgo(15),
    journey: journey({
      kind: 'Outpatient',
      id: '0192a000-0000-7000-8000-00000000a502',
      number: 'OP2600990',
      stages: [
        stage('checked-in', 'Checked in', 'Current', 15, 'Token GM-015'),
        stage('called', 'Called', 'Pending'),
        stage('in-consultation', 'In consultation', 'Pending'),
        stage('consultation-signed', 'Consultation signed', 'Pending'),
        stage('investigations-done', 'Investigations done', 'Pending'),
        stage('dispensed', 'Medicines dispensed', 'Pending'),
        stage('billed', 'Billed', 'Pending', null, '2 services not billed yet'),
        stage('paid', 'Paid', 'Pending'),
        stage('closed', 'Visit closed', 'Pending'),
      ],
    }),
  },
  {
    patientId: '0192a000-0000-7000-8000-000000000004',
    uhid: 'NH26000126',
    patientName: 'Sunil Patil',
    isRestricted: false,
    gender: 'Male',
    age: '58Y',
    kind: 'Inpatient',
    departmentId: OPD.id,
    departmentName: OPD.name,
    wardId: WARD.id,
    place: `${WARD.name} · GWM-07`,
    arrivedAt: minutesAgo(60 * 50),
    journey: journey({
      kind: 'Inpatient',
      id: '0192a000-0000-7000-8000-00000000a503',
      number: 'IP2600112',
      title: `${WARD.name} · GWM-07`,
      stages: [
        stage('admitted', 'Admitted', 'Done', 60 * 50),
        stage('bedded', 'In a bed', 'Done', 60 * 50, `${WARD.name} · GWM-07`),
        stage('rounds-and-nursing', 'Rounds and nursing', 'Current', 60 * 44),
        stage('investigations-complete', 'Investigations complete', 'Pending', null, '1 of 2 resulted'),
        stage('discharge-initiated', 'Discharge initiated', 'Pending'),
        stage('summary-signed', 'Discharge summary signed', 'Pending'),
        stage('cleared', 'Cleared by billing', 'Pending', null, '3 services not billed yet'),
        stage('discharged', 'Discharged', 'Pending'),
        stage('bed-cleaned', 'Bed cleaned', 'Pending'),
      ],
    }),
  },
];

/** A patient's journeys as their page shows them: Asha's visit, her CRP waiting in the lab, and a booked operation. */
export function patientJourneys(patientId: string): PatientJourneys {
  return {
    patientId,
    asOf: minutesAgo(0),
    outpatient: ASHA_VISIT,
    inpatient: null,
    surgeries: [
      journey({
        kind: 'Surgery',
        id: '0192a000-0000-7000-8000-00000000a504',
        number: 'OT2600045',
        title: 'Laparoscopic cholecystectomy',
        stages: [
          stage('requested', 'Requested', 'Done', 60 * 26),
          stage('scheduled', 'Scheduled', 'Current', null, 'Slot 01 Nov 2026, 09:00 AM IST in OT-2'),
          stage('pre-op', 'Pre-op checks and consent', 'Pending', null, '1 of 10 checks done'),
          stage('received', 'Received in pre-op', 'Pending'),
          stage('sign-in', 'WHO Sign-in', 'Pending'),
          stage('time-out', 'WHO Time-out', 'Pending'),
          stage('incision', 'Incision', 'Pending'),
          stage('sign-out', 'WHO Sign-out', 'Pending'),
          stage('out-and-billed', 'Out of theatre and billed', 'Pending'),
        ],
      }),
    ],
    labOrders: [
      journey({
        kind: 'Lab',
        id: '0192a000-0000-7000-8000-00000000a505',
        number: 'NHLB260000123',
        title: 'C-reactive protein (CRP)',
        stages: [
          stage('ordered', 'Ordered', 'Done', 18, '1 test'),
          stage('payment-cleared', 'Payment cleared', 'Done', 12),
          stage('collected', 'Sample collected', 'Current', 5),
          stage('received', 'Received in lab', 'Pending'),
          stage('resulted', 'Resulted', 'Pending'),
          stage('verified', 'Verified', 'Pending'),
          stage('released', 'Report released', 'Pending'),
          stage('reviewed', 'Reviewed by the doctor', 'Pending'),
        ],
      }),
    ],
    surgeriesWithheld: false,
  };
}

export const FRONT_OFFICE_ANSWERS: FakeAnswers = {
  'GET /api/v1/journeys/in-hospital': paged(IN_HOSPITAL),
  'GET /api/v1/inpatient/wards': [WARD],
  'GET /api/v1/patients': paged(PATIENTS),
  'GET /api/v1/masters/lookups/departments': [OPD],
  'GET /api/v1/masters/lookups/practitioners': DOCTORS,
  'GET /api/v1/queue': QUEUE,
  'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
  'GET /api/v1/scheduling/appointments': paged(APPOINTMENTS),
  'GET /api/v1/scheduling/appointments/summary': SUMMARY,
  'GET /api/v1/billing/bills': paged(BILLS),
};

/** A front-desk user: patients, the queue, appointments and the OP billing counter (their home page's widgets). */
export const FRONT_DESK_USER: AuthUser = {
  ...STAFF_USER,
  id: '0192a000-0000-7000-8000-0000000000a2',
  userName: 'kavita.shah',
  displayName: 'Kavita Shah',
  practitionerId: null,
  roles: ['FrontDesk'],
  permissions: [
    'patients.read',
    'patients.register',
    'visits.read',
    'frontoffice.dashboard',
    'queue.manage',
    'scheduling.appointments.read',
    'scheduling.appointments.book',
    'billing.op.create',
    'billing.payments.collect',
    'billing.shifts',
  ],
  mfaEnabled: false,
};

export const FRONT_OFFICE_DASHBOARD: FrontOfficeDashboard = {
  registrationsToday: 18,
  visitsToday: 64,
  openVisits: 21,
  completedVisits: 43,
  waitingTokens: 9,
  averageWaitMinutes: 17,
  openEnquiries: 3,
  visitorsInside: 12,
  departments: [
    { departmentId: OPD.id, departmentName: OPD.name, waiting: 6, served: 31, nowServing: 'GM-012' },
    {
      departmentId: '0192a000-0000-7000-8000-00000000de02',
      departmentName: 'Cardiology',
      waiting: 3,
      served: 12,
      nowServing: null,
    },
  ],
  visitsByType: [
    { type: 'OutPatient', count: 52 },
    { type: 'Emergency', count: 4 },
    { type: 'Telehealth', count: 8 },
  ],
};

/** The hospital's calendar date (IST) today, "yyyy-MM-dd". */
const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata' }).format(new Date());

/** Seven in the morning today (IST), before the OPD's first slot. */
export const CALENDAR_MORNING = `${today}T07:00:00+05:30`;

export const DAY_END: DayEndReport = {
  date: today,
  counter: null,
  shifts: [
    {
      shiftId: '0192a000-0000-7000-8000-000000005001',
      userId: '0192a000-0000-7000-8000-0000000000a2',
      userName: 'Kavita Shah',
      counter: 'OPD-1',
      status: 'Open',
      openedAt: minutesAgo(300),
      closedAt: null,
      collected: 48250,
      refunded: 500,
      expectedCash: 21300,
      declaredCash: null,
      variance: null,
    },
  ],
  byMode: [
    { mode: 'Cash', collected: 21800, refunded: 500, net: 21300 },
    { mode: 'Upi', collected: 19450, refunded: 0, net: 19450 },
    { mode: 'Card', collected: 7000, refunded: 0, net: 7000 },
  ],
  totalCollected: 48250,
  totalRefunded: 500,
  netCollection: 47750,
  depositsCollected: 10000,
  billsFinalized: 41,
  billedAmount: 52400,
  discountGiven: 1200,
  billsCancelled: 1,
  cancelledAmount: 350,
  openShifts: 1,
};

/** What the front desk's home page asks for: the front office, today's appointments and today's collections. */
export const FRONT_DESK_HOME_ANSWERS: FakeAnswers = {
  ...FRONT_OFFICE_ANSWERS,
  'GET /api/v1/front-office/dashboard': FRONT_OFFICE_DASHBOARD,
  'GET /api/v1/billing/reports/day-end': DAY_END,
};

/** A session of 15-minute slots from `start` to `end` (hours, IST) today, the first `booked` of them taken. */
function session(start: number, end: number, booked: number, room: string) {
  const slots: SlotDto[] = [];
  for (let minutes = start * 60; minutes < end * 60; minutes += 15) {
    const time = (at: number) =>
      `${String(Math.floor(at / 60)).padStart(2, '0')}:${String(at % 60).padStart(2, '0')}:00`;
    const taken = slots.length < booked ? 1 : 0;
    slots.push({
      startsAt: `${today}T${time(minutes)}+05:30`,
      endsAt: `${today}T${time(minutes + 15)}+05:30`,
      date: today,
      localStart: time(minutes),
      localEnd: time(minutes + 15),
      capacity: 1,
      booked: taken,
      available: 1 - taken,
      mode: 'InPerson',
      facilityId: FACILITY_ID,
      departmentId: OPD.id,
      room,
    });
  }
  return slots;
}

export const AVAILABILITY: PractitionerAvailability[] = [
  {
    practitionerId: DOCTORS[0]!.id,
    practitionerName: DOCTORS[0]!.name,
    departmentId: OPD.id,
    departmentName: OPD.name,
    offersTelehealth: true,
    freeSlots: 13,
    slots: session(9, 13, 3, 'Room 3'),
  },
  {
    practitionerId: DOCTORS[1]!.id,
    practitionerName: DOCTORS[1]!.name,
    departmentId: OPD.id,
    departmentName: OPD.name,
    offersTelehealth: false,
    freeSlots: 7,
    slots: session(10, 12, 1, 'Room 5'),
  },
];

const booked = (
  overrides: Partial<CalendarAppointment> & Pick<CalendarAppointment, 'id' | 'number' | 'startsAt' | 'endsAt'>,
): CalendarAppointment => ({
  patientId: PATIENTS[0]!.id,
  patientName: 'Asha Devi',
  uhid: 'NH26000123',
  practitionerId: DOCTORS[0]!.id,
  departmentId: OPD.id,
  status: 'Booked',
  visitType: 'New',
  channel: 'Phone',
  isTelehealth: false,
  paymentStatus: 'PayAtHospital',
  ...overrides,
});

export const CALENDAR: CalendarAppointment[] = [
  booked({
    id: 'c1',
    number: 'AP2600111',
    startsAt: `${today}T09:00:00+05:30`,
    endsAt: `${today}T09:15:00+05:30`,
    status: 'CheckedIn',
  }),
  booked({
    id: 'c2',
    number: 'AP2600112',
    startsAt: `${today}T09:15:00+05:30`,
    endsAt: `${today}T09:30:00+05:30`,
    patientId: PATIENTS[1]!.id,
    patientName: 'Ravi Shankar',
    uhid: 'NH26000124',
    status: 'Confirmed',
    visitType: 'FollowUp',
  }),
  booked({
    id: 'c3',
    number: 'AP2600113',
    startsAt: `${today}T09:30:00+05:30`,
    endsAt: `${today}T09:45:00+05:30`,
    isTelehealth: true,
    channel: 'Portal',
    paymentStatus: 'PaidOnline',
  }),
  booked({
    id: 'c4',
    number: 'AP2600114',
    startsAt: `${today}T10:00:00+05:30`,
    endsAt: `${today}T10:15:00+05:30`,
    practitionerId: DOCTORS[1]!.id,
    patientName: 'Kamala Bai',
    uhid: 'NH26000125',
    status: 'NoShow',
  }),
];

/** The appointment calendar of general medicine today: two doctors' sessions, bookings and free slots. */
export const CALENDAR_ANSWERS: FakeAnswers = {
  ...FRONT_OFFICE_ANSWERS,
  'GET /api/v1/scheduling/slots': AVAILABILITY,
  'GET /api/v1/scheduling/appointments/calendar': CALENDAR,
  'GET /api/v1/scheduling/exceptions': [],
};
