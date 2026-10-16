import type { AuthUser } from '@hc360/core';
import type { FakeAnswers } from '@hc360/ui/testing/fake-api';
import type {
  CommunicationPreferences,
  DirectoryFilters,
  OtpChallenge,
  PortalAppointment,
  PortalBillListItem,
  PortalPrescriptionListItem,
  PortalProfile,
  PortalReportItem,
  PortalTelehealthSession,
  PortalWaitlistEntry,
  PublicDoctor,
  PublicDoctorProfile,
  PublicSlotDay,
} from '@/lib/types';

// The portal's pages with canned data, for the accessibility checks in jsdom (accessibility.test.tsx) and in a real
// browser (a11y/*.spec.ts). No test framework in here.

export const PATIENT_ID = '5f0c6d3e-8c1a-4a53-9a3f-2b1d6f7e9a10';
export const DOCTOR_ID = '7b8c9d0e-1f2a-4b3c-8d4e-5f6a7b8c9d0e';
const hours = (count: number) => new Date(Date.now() + count * 3600_000).toISOString();

export const PROFILE: PortalProfile = {
  id: PATIENT_ID,
  uhid: 'NH26000123',
  fullName: 'Asha Verma',
  gender: 'Female',
  dateOfBirth: '1987-03-14',
  age: '39Y',
  phone: '+91******3210',
  bloodGroup: 'BPositive',
  category: 'General',
  isVip: false,
  isDeceased: false,
  photoFileId: null,
};

export const USER: AuthUser = {
  id: 'user-1',
  userName: '+919876543210',
  displayName: 'Asha Verma',
  type: 'patient',
  facilityId: null,
  facilityName: null,
  practitionerId: null,
  patientId: PATIENT_ID,
  roles: [],
  permissions: ['portal.access'],
  facilities: [],
  mfaEnabled: false,
  hasPin: false,
  mustChangePassword: false,
  restriction: null,
};

const appointment = (overrides: Partial<PortalAppointment> & Pick<PortalAppointment, 'id' | 'number'>) =>
  ({
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Meera Iyer',
    specialtyName: 'General medicine',
    departmentName: 'General medicine',
    facilityId: 'f1',
    facilityName: 'Nana Hospitals, Pune',
    startsAt: hours(26),
    endsAt: hours(26.25),
    status: 'Booked',
    visitType: 'New',
    isTelehealth: false,
    paymentStatus: 'PayAtHospital',
    feeAmount: 500,
    currency: 'INR',
    reason: 'Knee pain',
    canCancel: true,
    canReschedule: true,
    canPay: false,
    ...overrides,
  }) satisfies PortalAppointment;

export const APPOINTMENTS: PortalAppointment[] = [
  appointment({ id: 'a1', number: 'AP2600101' }),
  appointment({
    id: 'a2',
    number: 'AP2600102',
    startsAt: hours(50),
    endsAt: hours(50.25),
    isTelehealth: true,
    paymentStatus: 'Unpaid',
    canPay: true,
    status: 'Confirmed',
  }),
];

export const WAITLIST: PortalWaitlistEntry[] = [
  {
    id: 'w1',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Anil Rao',
    preferredFrom: '2026-10-12',
    preferredTo: '2026-10-19',
    preferredSession: 'Morning',
    isTelehealth: false,
    status: 'Waiting',
    offeredSlotStartsAt: null,
    createdAt: hours(-30),
  },
];

export const REPORTS: PortalReportItem[] = [
  {
    kind: 'Laboratory',
    id: 'r1',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    title: 'Complete blood count',
    reference: 'LAB2600123',
    releasedAt: hours(-20),
    isAmended: false,
    isPartial: true,
  },
  {
    kind: 'Radiology',
    id: 'r2',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    title: 'Chest X-ray',
    reference: 'RAD2600045',
    releasedAt: hours(-200),
    isAmended: true,
    isPartial: false,
  },
  {
    kind: 'Cardiology',
    id: 'r3',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    title: 'Resting ECG',
    reference: 'CAR2600007',
    releasedAt: hours(-400),
    isAmended: false,
    isPartial: false,
  },
];

export const BILLS: PortalBillListItem[] = [
  {
    id: 'b1',
    billNumber: 'OP2600001',
    type: 'OutPatient',
    status: 'PartiallyPaid',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    billedAt: hours(-48),
    netAmount: 1250,
    patientAmount: 1250,
    paidAmount: 500,
    dueAmount: 750,
  },
];

export const PRESCRIPTIONS: PortalPrescriptionListItem[] = [
  {
    id: 'p1',
    prescriptionNumber: 'RX2600123',
    patientId: PATIENT_ID,
    status: 'Signed',
    signedAt: hours(-24),
    doctor: 'Dr. Meera Iyer',
    medicines: ['Paracetamol 500 mg', 'Pantoprazole 40 mg'],
    refillsRemaining: 1,
  },
];

export const VIDEO: PortalTelehealthSession[] = [
  {
    id: 'v1',
    patientId: PATIENT_ID,
    patientName: 'Asha Verma',
    practitionerId: DOCTOR_ID,
    practitionerName: 'Dr. Meera Iyer',
    appointmentNumber: 'AP2600102',
    status: 'Scheduled',
    scheduledStartAt: hours(50),
    scheduledEndAt: hours(50.25),
    joinOpensAt: hours(49.75),
    canJoinNow: false,
  },
];

export const CHALLENGE: OtpChallenge = { challengeId: 'c1', expiresAt: hours(0.1), maskedPhone: '+91******3210' };

const FACILITY = { id: 'f1', name: 'Nana Hospitals, Pune', city: 'Pune' };

export const DOCTOR: PublicDoctor = {
  id: DOCTOR_ID,
  name: 'Dr. Meera Iyer',
  gender: 'Female',
  qualification: 'MBBS, MD (General Medicine)',
  specialtyId: 's1',
  specialtyName: 'General medicine',
  departmentId: 'd1',
  departmentName: 'General medicine',
  languages: ['English', 'Hindi', 'Marathi'],
  feeNew: 500,
  feeFollowUp: 300,
  currency: 'INR',
  offersTelehealth: true,
  facilities: [FACILITY],
  nextAvailableAt: hours(26),
  nextTelehealthAt: hours(50),
};

export const DOCTORS: PublicDoctor[] = [
  DOCTOR,
  {
    ...DOCTOR,
    id: '7b8c9d0e-1f2a-4b3c-8d4e-5f6a7b8c9d0f',
    name: 'Dr. Anil Rao',
    gender: 'Male',
    qualification: 'MBBS, MS (Orthopaedics)',
    specialtyId: 's2',
    specialtyName: 'Orthopaedics',
    departmentName: 'Orthopaedics',
    languages: ['English', 'Kannada'],
    feeNew: 700,
    offersTelehealth: false,
    nextTelehealthAt: null,
  },
];

export const DIRECTORY_FILTERS: DirectoryFilters = {
  specialties: [
    { id: 's1', name: 'General medicine', doctors: 1 },
    { id: 's2', name: 'Orthopaedics', doctors: 1 },
  ],
  departments: [
    { id: 'd1', name: 'General medicine', doctors: 1 },
    { id: 'd2', name: 'Orthopaedics', doctors: 1 },
  ],
  languages: ['English', 'Hindi', 'Kannada', 'Marathi'],
  facilities: [FACILITY],
  cutoffMinutes: 60,
  windowDays: 30,
};

export const DOCTOR_PROFILE: PublicDoctorProfile = {
  doctor: DOCTOR,
  bio: 'Fifteen years in general medicine: fevers, diabetes, blood pressure and preventive check-ups.',
  followUpValidityDays: 7,
  weeklySessions: [
    { weekday: 'Monday', startTime: '09:00:00', endTime: '13:00:00', mode: 'InPerson', facilityId: FACILITY.id },
    { weekday: 'Wednesday', startTime: '16:00:00', endTime: '18:00:00', mode: 'Both', facilityId: FACILITY.id },
  ],
};

/** The hospital's calendar date (IST) `days` from today, "yyyy-MM-dd". */
const istDate = (days: number) =>
  new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Kolkata' }).format(new Date(Date.now() + days * 86_400_000));

/** Free seats over the next week: three morning slots a day, in person. */
export const SLOT_DAYS: PublicSlotDay[] = [1, 2, 3, 4, 5, 6, 7].map((days) => ({
  date: istDate(days),
  slots: ['09:00', '09:15', '09:30'].map((time, index) => ({
    startsAt: `${istDate(days)}T${time}:00+05:30`,
    endsAt: `${istDate(days)}T${time.slice(0, 3)}${String(Number(time.slice(3)) + 15).padStart(2, '0')}:00+05:30`,
    localTime: time,
    mode: 'InPerson',
    facilityId: FACILITY.id,
    seatsLeft: 3 - index,
  })),
}));

/** Reminders and updates on, offers off; WhatsApp on, changeable as the patient signed in with their own number. */
export const PREFERENCES: CommunicationPreferences = {
  marketing: false,
  updates: true,
  marketingChangedAt: hours(-24 * 30),
  updatesChangedAt: null,
  whatsApp: true,
  whatsAppChangedAt: hours(-24 * 10),
  whatsAppEditable: true,
};

/** What the portal's pages ask the server for, signed in as USER. */
export const PORTAL_ANSWERS: FakeAnswers = {
  'GET /api/v1/system/info': { facilityName: 'Nana Hospitals', features: { patientPortal: true, telehealth: true } },
  // The anti-automation check (lib/proofOfWork): a challenge with almost no work, which this server does not check.
  'POST /api/v1/public/challenge': { required: true, challenge: '1.otp.1.1.00.sig', bits: 1, expiresAt: null },
  'POST /api/v1/auth/otp/request': CHALLENGE,
  'GET /api/v1/portal/profiles': [PROFILE],
  'GET /api/v1/portal/appointments': APPOINTMENTS,
  'GET /api/v1/portal/waitlist': WAITLIST,
  'GET /api/v1/portal/reports': REPORTS,
  'GET /api/v1/portal/bills': { items: BILLS, page: 1, pageSize: 50, totalCount: BILLS.length },
  'GET /api/v1/portal/prescriptions': { items: PRESCRIPTIONS, page: 1, pageSize: 20, totalCount: PRESCRIPTIONS.length },
  'GET /api/v1/portal/telehealth/sessions': VIDEO,
  'GET /api/v1/portal/privacy/notices': [],
  'GET /api/v1/portal/communication-preferences': PREFERENCES,
  'GET /api/v1/public/doctors/filters': DIRECTORY_FILTERS,
  'GET /api/v1/public/doctors': { items: DOCTORS, page: 1, pageSize: 12, totalCount: DOCTORS.length },
  [`GET /api/v1/public/doctors/${DOCTOR_ID}`]: DOCTOR_PROFILE,
  [`GET /api/v1/public/doctors/${DOCTOR_ID}/slots`]: SLOT_DAYS,
};
