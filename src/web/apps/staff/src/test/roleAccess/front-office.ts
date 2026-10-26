import type { AbhaShareRow } from '@/modules/front-office/abhaShares';
import type { DisplayDepartment, EnquiryDto, VisitorPassDto } from '@/modules/front-office/api';
import type { VisitDetail, VisitListItem } from '@/modules/patients/api';
import { PATIENTS } from '../accessibility/fixtures/frontOffice';
import { paged, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The front desk: today's visits (open ones can be closed or cancelled), enquiries, visitor passes (an active pass can
// be checked out or revoked) and the waiting-hall display.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000072${suffix}`;
const OPD = { id: '0192a000-0000-7000-8000-00000000de01', name: 'General medicine' };
const [ASHA, RAVI, KAMALA] = [PATIENTS[0]!, PATIENTS[1]!, PATIENTS[2]!];

const visit = (overrides: Partial<VisitListItem> & Pick<VisitListItem, 'id' | 'visitNumber'>): VisitListItem => ({
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  gender: ASHA.gender,
  age: ASHA.age,
  type: 'OutPatient',
  status: 'Open',
  departmentId: OPD.id,
  departmentName: OPD.name,
  practitionerId: STAFF_USER.practitionerId,
  practitionerName: 'Dr. Meera Iyer',
  visitedAt: minutesAgo(50),
  completedAt: null,
  isFollowUp: false,
  isFreeFollowUp: false,
  payerType: 'Self',
  isMlc: false,
  tokenLabel: 'GM-012',
  tokenStatus: 'Waiting',
  ...overrides,
});

export const VISITS: VisitListItem[] = [
  visit({ id: RECORD_ID, visitNumber: 'OP2600981' }),
  visit({
    id: id('02'),
    visitNumber: 'OP2600982',
    patientId: RAVI.id,
    uhid: RAVI.uhid,
    patientName: RAVI.fullName,
    gender: RAVI.gender,
    age: RAVI.age,
    status: 'InProgress',
    payerType: 'Staff',
    tokenLabel: 'GM-013',
    tokenStatus: 'InService',
    isFollowUp: true,
    isFreeFollowUp: true,
  }),
  visit({
    id: id('03'),
    visitNumber: 'IP2600112',
    patientId: KAMALA.id,
    uhid: KAMALA.uhid,
    patientName: KAMALA.fullName,
    gender: KAMALA.gender,
    age: KAMALA.age,
    type: 'InPatient',
    status: 'InProgress',
    payerType: 'Insurance',
    tokenLabel: null,
    tokenStatus: null,
    visitedAt: minutesAgo(60 * 26),
  }),
  visit({
    id: id('04'),
    visitNumber: 'OP2600975',
    status: 'Completed',
    visitedAt: minutesAgo(180),
    completedAt: minutesAgo(140),
    tokenLabel: 'GM-004',
    tokenStatus: 'Completed',
  }),
];

const VISIT: VisitDetail = {
  id: RECORD_ID,
  visitNumber: 'OP2600981',
  patientId: ASHA.id,
  type: 'OutPatient',
  status: 'Open',
  departmentId: OPD.id,
  practitionerId: STAFF_USER.practitionerId,
  appointmentId: null,
  visitedAt: minutesAgo(50),
  completedAt: null,
  isFollowUp: false,
  isFreeFollowUp: false,
  previousVisitId: null,
  payerType: 'Self',
  payerId: null,
  referralSource: 'Self',
  referredBy: null,
  chiefComplaint: 'Fever for three days',
  isMlc: false,
  mlcNumber: null,
  notes: null,
  cancelReason: null,
  token: {
    id: id('20'),
    visitId: RECORD_ID,
    patientId: ASHA.id,
    departmentId: OPD.id,
    practitionerId: STAFF_USER.practitionerId,
    tokenDate: minutesAgo(50).slice(0, 10),
    sequence: 12,
    label: 'GM-012',
    status: 'Waiting',
    priority: 'Normal',
    issuedAt: minutesAgo(50),
    calledAt: null,
    serviceStartedAt: null,
    completedAt: null,
    room: 'Room 3',
    callCount: 0,
  },
  version: 2,
};

const ENQUIRIES: EnquiryDto[] = [
  {
    id: RECORD_ID,
    name: 'Sunita Joshi',
    phone: '9822012345',
    type: 'DoctorAvailability',
    channel: 'Phone',
    details: 'Is the cardiologist available on Saturday morning?',
    status: 'Open',
    response: null,
    patientId: null,
    createdAt: minutesAgo(35),
    resolvedAt: null,
    version: 1,
  },
  {
    id: id('e2'),
    name: 'Mohammed Shaikh',
    phone: '9890076543',
    type: 'HealthPackage',
    channel: 'WhatsApp',
    details: 'Price of the executive health check for two people.',
    status: 'InProgress',
    response: 'Shared the brochure; waiting for dates.',
    patientId: null,
    createdAt: minutesAgo(200),
    resolvedAt: null,
    version: 2,
  },
];

const pass = (overrides: Partial<VisitorPassDto> & Pick<VisitorPassDto, 'id' | 'passNumber'>): VisitorPassDto => ({
  patientId: KAMALA.id,
  patientName: KAMALA.fullName,
  patientUhid: KAMALA.uhid,
  visitorName: 'Ramesh Bai',
  phone: '98******44',
  relation: 'Son',
  idProofType: 'Aadhaar',
  idProofLast4: '4821',
  purpose: 'Attendant',
  location: 'General Ward — Female, bed 7',
  isAttendant: true,
  validFrom: minutesAgo(120),
  validUntil: minutesAgo(-600),
  status: 'Active',
  checkedOutAt: null,
  ...overrides,
});

const PASSES: VisitorPassDto[] = [
  pass({ id: RECORD_ID, passNumber: 'VP2600341' }),
  pass({
    id: id('21'),
    passNumber: 'VP2600338',
    visitorName: 'Lata Shankar',
    relation: 'Wife',
    patientId: RAVI.id,
    patientName: RAVI.fullName,
    patientUhid: RAVI.uhid,
    isAttendant: false,
    purpose: 'Visiting',
    status: 'CheckedOut',
    checkedOutAt: minutesAgo(30),
  }),
];

const DISPLAY: DisplayDepartment[] = [
  {
    departmentId: OPD.id,
    departmentName: OPD.name,
    nowServing: [{ label: 'GM-011', maskedName: 'A*** D***', room: 'Room 3', calledAt: minutesAgo(4) }],
    upNext: ['GM-012', 'GM-013'],
    waiting: 6,
  },
];

// Scan and share: one new patient waiting to be registered, one matched to their record, one already done.
const share = (overrides: Partial<AbhaShareRow> & Pick<AbhaShareRow, 'id' | 'tokenNumber'>): AbhaShareRow => ({
  counter: 'REG-1',
  name: 'Arjun Rao',
  gender: 'M',
  yearOfBirth: 1990,
  abhaAddress: 'arjun.rao@abdm',
  abhaNumberMasked: 'xx-xxxx-xxxx-4821',
  phoneMasked: '+91******3210',
  status: 'Waiting',
  sharedAt: minutesAgo(6),
  expiresAt: minutesAgo(-54),
  patient: null,
  matchedBy: null,
  ...overrides,
});

const SHARES: AbhaShareRow[] = [
  share({ id: RECORD_ID, tokenNumber: 7 }),
  share({
    id: id('31'),
    tokenNumber: 8,
    name: ASHA.fullName,
    gender: 'F',
    abhaAddress: 'asha.d@abdm',
    patient: { id: ASHA.id, uhid: ASHA.uhid, fullName: ASHA.fullName },
    matchedBy: 'ABHA_ADDRESS',
  }),
  share({
    id: id('32'),
    tokenNumber: 3,
    status: 'Registered',
    name: RAVI.fullName,
    abhaAddress: 'ravi.k@abdm',
    sharedAt: minutesAgo(90),
    patient: { id: RAVI.id, uhid: RAVI.uhid, fullName: RAVI.fullName },
  }),
];

export const ANSWERS: FakeAnswers = {
  // ABDM is off until the hospital turns it on; the hospital in this check has.
  'GET /api/v1/settings/public': { 'features.abdm': true },
  'GET /api/v1/abdm/shares': SHARES,
  'GET /api/v1/visits': paged(VISITS),
  [`GET /api/v1/visits/${RECORD_ID}`]: VISIT,
  'GET /api/v1/front-office/enquiries': paged(ENQUIRIES),
  'GET /api/v1/front-office/visitor-passes': paged(PASSES),
  'GET /api/v1/queue/display': DISPLAY,
};
