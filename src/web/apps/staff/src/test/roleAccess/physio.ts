import type {
  PhysioReferralDetail,
  PhysioReferralListItem,
  PhysioSessionDto,
  PhysioWorklistItem,
} from '@/modules/physio/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Physiotherapy: today's sessions (scheduled and attended), referrals in each open state, and one referral in
// treatment with a plan, a scheduled session and an attended one whose charge failed to post.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3_600_000).toISOString();
const daysAgo = (days: number) => hoursAgo(days * 24);

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000a${suffix}`;
const RAVI = '0192a000-0000-7000-8000-000000000002';
const ASHA = '0192a000-0000-7000-8000-000000000001';

const referral = (
  overrides: Partial<PhysioReferralListItem> &
    Pick<PhysioReferralListItem, 'id' | 'patientId' | 'uhid' | 'patientName' | 'status'>,
): PhysioReferralListItem => ({
  gender: 'Male',
  age: '62Y',
  serviceName: 'Physiotherapy session',
  reason: 'Post total knee replacement rehabilitation',
  sessionsOrdered: 10,
  priority: 'Routine',
  referredAt: daysAgo(6),
  referredByPractitionerId: '0192a000-0000-7000-8000-0000000000d1',
  referredByName: 'Dr. Meera Iyer',
  assignedTherapistName: 'Kavita Joshi (PT)',
  isInpatient: false,
  ...overrides,
});

const REFERRALS: PhysioReferralListItem[] = [
  referral({ id: RECORD_ID, patientId: RAVI, uhid: 'NH26000124', patientName: 'Ravi Shankar', status: 'InTreatment' }),
  referral({
    id: id('102'),
    patientId: ASHA,
    uhid: 'NH26000123',
    patientName: 'Asha Devi',
    gender: 'Female',
    age: '39Y',
    reason: 'Frozen shoulder, right',
    sessionsOrdered: 8,
    status: 'New',
    priority: 'Urgent',
    referredAt: hoursAgo(3),
    assignedTherapistName: null,
  }),
  referral({
    id: id('103'),
    patientId: '0192a000-0000-7000-8000-000000000006',
    uhid: 'NH26000077',
    patientName: 'Suresh Kumar',
    age: '71Y',
    reason: 'Chest physiotherapy after CABG',
    status: 'Assessed',
    isInpatient: true,
    referredAt: daysAgo(1),
  }),
];

const SESSIONS: PhysioWorklistItem[] = [
  {
    sessionId: id('201'),
    referralId: RECORD_ID,
    patientId: RAVI,
    uhid: 'NH26000124',
    patientName: 'Ravi Shankar',
    sessionNumber: 4,
    sessionsPlanned: 10,
    scheduledAt: hoursAgo(-1),
    status: 'Scheduled',
    therapistName: 'Kavita Joshi (PT)',
    version: 1,
  },
  {
    sessionId: id('202'),
    referralId: id('103'),
    patientId: '0192a000-0000-7000-8000-000000000006',
    uhid: 'NH26000077',
    patientName: 'Suresh Kumar',
    sessionNumber: 1,
    sessionsPlanned: 6,
    scheduledAt: hoursAgo(2),
    status: 'Attended',
    therapistName: 'Kavita Joshi (PT)',
    version: 2,
  },
];

const session = (
  overrides: Partial<PhysioSessionDto> & Pick<PhysioSessionDto, 'id' | 'sessionNumber' | 'scheduledAt' | 'status'>,
): PhysioSessionDto => ({
  referralId: RECORD_ID,
  patientId: RAVI,
  modalitiesGiven: [],
  progressNote: null,
  painBefore: null,
  painAfter: null,
  durationMinutes: null,
  attendedAt: null,
  therapistName: 'Kavita Joshi (PT)',
  missedReason: null,
  chargeStatus: 'NotDue',
  chargeError: null,
  version: 1,
  ...overrides,
});

const REFERRAL: PhysioReferralDetail = {
  id: RECORD_ID,
  patient: {
    id: RAVI,
    uhid: 'NH26000124',
    fullName: 'Ravi Shankar',
    gender: 'Male',
    dateOfBirth: null,
    age: '62Y',
    phone: '9822012345',
    bloodGroup: 'BPositive',
    category: 'General',
    isVip: false,
    isDeceased: false,
    photoFileId: null,
  },
  patientId: RAVI,
  visitId: '0192a000-0000-7000-8000-000000000e15',
  admissionId: null,
  orderItemId: id('301'),
  orderNumber: 'ORD2600877',
  serviceName: 'Physiotherapy session',
  sessionsOrdered: 10,
  referredByPractitionerId: '0192a000-0000-7000-8000-0000000000d1',
  referredByName: 'Dr. Meera Iyer',
  priority: 'Routine',
  reason: 'Post total knee replacement rehabilitation (left)',
  referredAt: daysAgo(6),
  status: 'InTreatment',
  assignedTherapistName: 'Kavita Joshi (PT)',
  dischargeSummary: null,
  closedAt: null,
  cancelReason: null,
  assessments: [
    {
      id: id('401'),
      type: 'Initial',
      assessedAt: daysAgo(5),
      assessedByName: 'Kavita Joshi (PT)',
      chiefComplaint: 'Pain and stiffness in the left knee after TKR',
      painScore: 6,
      painLocation: 'Left knee',
      rangeOfMotion: [
        { joint: 'Knee', movement: 'Flexion', side: 'Left', activeDegrees: 70, passiveDegrees: 85 },
        { joint: 'Knee', movement: 'Extension', side: 'Left', activeDegrees: -10, passiveDegrees: -5 },
      ],
      strength: [{ muscleGroup: 'Quadriceps', side: 'Left', mrcGrade: 3 }],
      functionalStatus: 'Walks with a walker for 20 metres',
      goals: 'Knee flexion 110°, walk without support',
      notes: null,
    },
  ],
  plan: {
    id: id('501'),
    modalities: ['IFT', 'Exercises', 'GaitTraining'],
    exerciseProgramme: 'Quadriceps sets, straight leg raises, heel slides — 10 each, three times a day',
    sessionsPlanned: 10,
    sessionsPerWeek: 3,
    sessionMinutes: 45,
    startDate: daysAgo(5).slice(0, 10),
    precautions: 'No deep squats',
    goals: 'Knee flexion 110°, walk without support',
    revision: 1,
    version: 1,
  },
  sessions: [
    // One attended session only: two draw the pain chart, which jsdom has no canvas for.
    session({
      id: id('601'),
      sessionNumber: 1,
      scheduledAt: daysAgo(4),
      status: 'Missed',
      missedReason: 'Patient had fever',
      version: 2,
    }),
    session({
      id: id('602'),
      sessionNumber: 2,
      scheduledAt: daysAgo(2),
      status: 'Attended',
      modalitiesGiven: ['IFT', 'Exercises', 'GaitTraining'],
      progressNote: 'Flexion 85°, walking with a stick',
      painBefore: 5,
      painAfter: 3,
      durationMinutes: 45,
      attendedAt: daysAgo(2),
      chargeStatus: 'Failed',
      chargeError: 'The visit is already billed',
      version: 2,
    }),
    session({ id: id('201'), sessionNumber: 4, scheduledAt: hoursAgo(-1), status: 'Scheduled' }),
  ],
  version: 5,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/physio/sessions': SESSIONS,
  'GET /api/v1/physio/referrals': paged(REFERRALS),
  [`GET /api/v1/physio/referrals/${RECORD_ID}`]: REFERRAL,
};
