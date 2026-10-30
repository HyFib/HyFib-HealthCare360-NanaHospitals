import { todayIso } from '@hc360/core';
import type {
  ChecklistItemDto,
  ImplantTraceItem,
  OtCalendar,
  OtCalendarBooking,
  OtStoreItem,
  OtUtilisationReport,
  SafetyPhaseDto,
  SurgeryDetail,
  SurgeryListItem,
  SurgeryMaterialDto,
  TheatreDto,
} from '@/modules/ot/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The operation theatres for today: theatres, the day's list with a case waiting for a slot, utilisation, and one
// scheduled surgery (ward checklist done, waiting to be received in pre-op) as the record.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const inMinutes = (minutes: number) => new Date(now + minutes * 60_000).toISOString();
const DAY = 24 * 60;
const today = todayIso();

const SURGEON = { surgeonId: '0192a000-0000-7000-8000-0000000000d1', surgeonName: 'Dr. Meera Iyer' };

const THEATRES: TheatreDto[] = [
  {
    id: id('a701'),
    code: 'OT-1',
    name: 'Main theatre 1',
    type: 'Major',
    equipment: ['Laparoscopy tower', 'C-arm', 'Electrosurgical unit'],
    location: '2nd floor, OT complex',
    displayOrder: 1,
    isActive: true,
    version: 2,
  },
  {
    id: id('a702'),
    code: 'OT-2',
    name: 'Orthopaedic theatre',
    type: 'Ortho',
    equipment: ['C-arm', 'Laminar flow'],
    location: '2nd floor, OT complex',
    displayOrder: 2,
    isActive: true,
    version: 1,
  },
  {
    id: id('a703'),
    code: 'OT-M',
    name: 'Minor OT',
    type: 'Minor',
    equipment: [],
    location: 'Casualty block',
    displayOrder: 3,
    isActive: false,
    version: 3,
  },
];

const ASHA = {
  patientId: '0192a000-0000-7000-8000-000000000001',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
} as const;
const RAVI = {
  patientId: '0192a000-0000-7000-8000-000000000002',
  uhid: 'NH26000124',
  patientName: 'Ravi Shankar',
  gender: 'Male',
  age: '62Y',
} as const;
const SURESH = {
  patientId: id('a7a3'),
  uhid: 'NH26000152',
  patientName: 'Suresh Patil',
  gender: 'Male',
  age: '47Y',
} as const;

const calendarBooking = (
  n: string,
  bookingNumber: string,
  who: typeof ASHA | typeof RAVI | typeof SURESH,
  procedureName: string,
  status: OtCalendarBooking['status'],
  theatreId: string | null,
  startIn: number | null,
  extra: Partial<OtCalendarBooking> = {},
): OtCalendarBooking => ({
  id: n === 'record' ? RECORD_ID : id(n),
  bookingNumber,
  ...who,
  procedureName,
  laterality: 'NotApplicable',
  priority: 'Elective',
  status,
  theatreId,
  start: startIn === null ? null : inMinutes(startIn),
  end: startIn === null ? null : inMinutes(startIn + 90),
  ...SURGEON,
  anaesthetistName: 'Dr. Rajesh Nair',
  anaesthesiaType: 'GA',
  signInDone: false,
  timeOutDone: false,
  signOutDone: false,
  ...extra,
});

const ON_LIST: OtCalendarBooking[] = [
  calendarBooking('record', 'OT26-00871', ASHA, 'Laparoscopic cholecystectomy', 'Scheduled', id('a701'), 60),
  calendarBooking('a7b2', 'OT26-00869', RAVI, 'Total knee replacement', 'InProgress', id('a702'), -45, {
    laterality: 'Right',
    anaesthesiaType: 'Spinal',
    signInDone: true,
    timeOutDone: true,
  }),
];
const AWAITING: OtCalendarBooking[] = [
  calendarBooking('a7b3', 'OT26-00874', SURESH, 'Inguinal hernia repair (mesh)', 'Requested', null, null, {
    laterality: 'Left',
    priority: 'Urgent',
  }),
];

const CALENDAR: OtCalendar = {
  from: today,
  to: today,
  theatres: THEATRES.filter((t) => t.isActive).map((t) => ({
    theatreId: t.id,
    code: t.code,
    name: t.name,
    type: t.type,
    bookings: ON_LIST.filter((b) => b.theatreId === t.id),
  })),
  awaitingSlot: AWAITING,
};

const UTILISATION: OtUtilisationReport = {
  from: today,
  to: today,
  minutesPerDay: 600,
  theatres: THEATRES.filter((t) => t.isActive).map((t, i) => ({
    theatreId: t.id,
    code: t.code,
    name: t.name,
    metrics: {
      theatreId: t.id,
      casesCompleted: 4 - i,
      casesCancelled: i,
      casesPostponed: 1,
      bookedMinutes: 420 - i * 60,
      actualMinutes: 395 - i * 50,
      actualToBookedPercent: 94,
      availableMinutes: 600,
      utilisationPercent: 66 - i * 8,
      firstCases: 1,
      firstCasesOnTime: 1 - i,
      firstCaseOnTimePercent: i === 0 ? 100 : 0,
      averageTurnoverMinutes: 28 + i * 7,
    },
  })),
};

const listItem = (b: OtCalendarBooking, theatreCode: string | null, chargeStatus: SurgeryListItem['chargeStatus']) =>
  ({
    id: b.id,
    bookingNumber: b.bookingNumber,
    patientId: b.patientId,
    uhid: b.uhid,
    patientName: b.patientName,
    gender: b.gender,
    age: b.age,
    procedureName: b.procedureName,
    laterality: b.laterality,
    priority: b.priority,
    status: b.status,
    theatreId: b.theatreId,
    theatreCode,
    scheduledStart: b.start,
    scheduledEnd: b.end,
    preferredDate: b.start ? null : todayIso(2),
    surgeonId: b.surgeonId,
    surgeonName: b.surgeonName,
    anaesthesiaType: b.anaesthesiaType,
    signInDone: b.signInDone,
    timeOutDone: b.timeOutDone,
    signOutDone: b.signOutDone,
    chargeStatus,
  }) satisfies SurgeryListItem;

const BOOKINGS: SurgeryListItem[] = [
  listItem(ON_LIST[0]!, 'OT-1', 'NotDue'),
  listItem(ON_LIST[1]!, 'OT-2', 'NotDue'),
  listItem(AWAITING[0]!, null, 'NotDue'),
];

const item = (key: string, text: string, state: ChecklistItemDto['state'] = 'Pending'): ChecklistItemDto => ({
  key,
  text,
  allowsNotApplicable: key === 'blood',
  state,
  remarks: null,
  recordedBy: state === 'Pending' ? null : 'Sr. Rekha Shinde',
  recordedAt: state === 'Pending' ? null : minutesAgo(40),
});

const phase = (p: SafetyPhaseDto['phase'], items: ChecklistItemDto[]): SafetyPhaseDto => ({
  phase: p,
  isComplete: false,
  completedAt: null,
  items,
});

const SURGERY: SurgeryDetail = {
  id: RECORD_ID,
  bookingNumber: 'OT26-00871',
  patient: {
    id: ASHA.patientId,
    uhid: ASHA.uhid,
    fullName: ASHA.patientName,
    gender: 'Female',
    dateOfBirth: '1987-03-14',
    age: '39Y',
    phone: '+91 98765 43210',
    bloodGroup: 'BPositive',
    category: 'General',
    isVip: false,
    isDeceased: false,
    photoFileId: null,
  },
  patientId: ASHA.patientId,
  admissionId: id('ad01'),
  visitId: null,
  orderItemId: null,
  procedureServiceItemId: id('a7c1'),
  procedureCode: 'SUR-LAPCHOLE',
  procedureName: 'Laparoscopic cholecystectomy',
  laterality: 'NotApplicable',
  diagnosis: 'Symptomatic cholelithiasis',
  ...SURGEON,
  assistantIds: [],
  anaesthetistId: id('a7d2'),
  anaesthetistName: 'Dr. Rajesh Nair',
  anaesthesiaType: 'GA',
  priority: 'Elective',
  preferredDate: null,
  theatreId: id('a701'),
  theatreName: 'Main theatre 1',
  scheduledStart: inMinutes(60),
  scheduledEnd: inMinutes(150),
  status: 'Scheduled',
  receivedInPreOpAt: null,
  inRoomAt: null,
  startedAt: null,
  completedAt: null,
  // The ward's checklist is done, so the screen opens on receiving the patient in pre-op.
  preOpComplete: true,
  preOpChecklist: [
    item('consent', 'Informed consent signed', 'Done'),
    item('npo', 'Nil by mouth since midnight', 'Done'),
    item('site', 'Surgical site marked', 'Done'),
    item('blood', 'Blood arranged (2 units PRBC cross-matched)', 'Done'),
  ],
  safetyChecklist: [
    phase('SignIn', [
      item('identity', 'Patient has confirmed identity, site, procedure and consent'),
      item('pulse-ox', 'Pulse oximeter on the patient and working'),
    ]),
    phase('TimeOut', [
      item('team', 'All team members introduced by name and role'),
      item('antibiotic', 'Antibiotic prophylaxis given within the last 60 minutes'),
    ]),
    phase('SignOut', [
      item('counts', 'Instrument, sponge and needle counts are correct'),
      item('specimen', 'Specimen labelled'),
    ]),
  ],
  notes: 'Diabetic — first on the list after the knee.',
  cancelReason: null,
  postponeReason: null,
  postponeCount: 0,
  chargeStatus: 'NotDue',
  chargeError: null,
  version: 4,
};

const MATERIALS: SurgeryMaterialDto[] = [
  {
    id: id('a7e1'),
    kind: 'Consumable',
    supplyItemId: id('a7f1'),
    itemName: 'Polymer ligating clips (Hem-o-lok), medium',
    manufacturer: 'Teleflex',
    lotNumber: 'HL24118',
    serialNumber: null,
    expiryDate: '2028-03-31',
    quantity: 6,
    unitPrice: 420,
    amount: 2520,
    recordedByName: 'Sr. Rekha Shinde',
    recordedAt: minutesAgo(10),
    chargeStatus: 'NotDue',
    chargeError: null,
    brand: 'Hem-o-lok',
    specification: 'Polymer ligating clip, medium',
    mrp: 450,
    ceilingPrice: null,
  },
];

const STORE_ITEMS: OtStoreItem[] = [
  {
    itemId: id('a7f1'),
    code: 'OT-CLIP-M',
    name: 'Polymer ligating clips (Hem-o-lok), medium',
    genericName: null,
    manufacturer: 'Teleflex',
    type: 'Consumable',
    baseUnit: 'Clip',
    available: 120,
    nearestExpiry: '2028-03-31',
    mrp: 420,
    batches: [{ batchId: id('a7f2'), batchNumber: 'HL24118', expiryDate: '2028-03-31', available: 120, mrp: 420 }],
  },
];

const TRACE: ImplantTraceItem[] = [
  {
    materialId: id('a7e2'),
    surgeryBookingId: id('a7b2'),
    bookingNumber: 'OT26-00869',
    patientId: RAVI.patientId,
    uhid: RAVI.uhid,
    patientName: RAVI.patientName,
    phone: '+91 97654 32121',
    itemName: 'Cemented knee prosthesis, femoral component, size 4',
    manufacturer: 'Zimmer Biomet',
    lotNumber: 'ZB2611A',
    serialNumber: 'ZB-FEM-004-88213',
    recordedAt: minutesAgo(DAY / 2),
  },
];

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/ot/theatres': THEATRES,
  'GET /api/v1/ot/calendar': CALENDAR,
  'GET /api/v1/ot/utilisation': UTILISATION,
  'GET /api/v1/ot/bookings': paged(BOOKINGS),
  [`GET /api/v1/ot/bookings/${RECORD_ID}`]: SURGERY,
  [`GET /api/v1/ot/bookings/${RECORD_ID}/materials`]: MATERIALS,
  'GET /api/v1/ot/store-items': STORE_ITEMS,
  'GET /api/v1/ot/implants/trace': TRACE,
};
