import type { BillListItem } from '@/modules/billing/api';
import type { QueueEntry } from '@/modules/front-office/api';
import type { PatientListItem } from '@/modules/patients/api';
import type { AppointmentDaySummary, AppointmentListItem } from '@/modules/scheduling/api';
import { paged, STAFF_USER, type FakeAnswers } from '../../fakeApi';

// The front desk's screens with canned data: patient search and registration, the queue, appointments and bills.

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

export const FRONT_OFFICE_ANSWERS: FakeAnswers = {
  'GET /api/v1/patients': paged(PATIENTS),
  'GET /api/v1/masters/lookups/departments': [OPD],
  'GET /api/v1/masters/lookups/practitioners': DOCTORS,
  'GET /api/v1/queue': QUEUE,
  'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
  'GET /api/v1/scheduling/appointments': paged(APPOINTMENTS),
  'GET /api/v1/scheduling/appointments/summary': SUMMARY,
  'GET /api/v1/billing/bills': paged(BILLS),
};
