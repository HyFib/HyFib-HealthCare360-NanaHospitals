import type {
  TelehealthConfig,
  TelehealthMessageDto,
  TelehealthSessionDetail,
  TelehealthSessionListItem,
} from '@/modules/virtualcare/telehealth/api';
import { FACILITY_ID, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Video consultations: today's sessions in each state that has its own actions (scheduled: re-send link and cancel;
// waiting: join; in progress: rejoin; completed: open only), and the one opened as a record.

const now = Date.now();
const minutesFromNow = (minutes: number) => new Date(now + minutes * 60_000).toISOString();
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000071${suffix}`;

const session = (
  overrides: Partial<TelehealthSessionListItem> & Pick<TelehealthSessionListItem, 'id' | 'status'>,
): TelehealthSessionListItem => ({
  appointmentId: id('a1'),
  appointmentNumber: 'AP2600451',
  patientId: '0192a000-0000-7000-8000-000000000001',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
  practitionerId: STAFF_USER.practitionerId!,
  practitionerName: 'Dr. Meera Iyer',
  scheduledStartAt: minutesFromNow(20),
  scheduledEndAt: minutesFromNow(35),
  patientWaitingSince: null,
  startedAt: null,
  endedAt: null,
  durationSeconds: null,
  visitId: null,
  linkSent: true,
  ...overrides,
});

const SESSIONS: TelehealthSessionListItem[] = [
  session({ id: RECORD_ID, status: 'Scheduled' }),
  session({
    id: id('02'),
    status: 'WaitingRoom',
    appointmentNumber: 'AP2600452',
    patientId: '0192a000-0000-7000-8000-000000000002',
    uhid: 'NH26000124',
    patientName: 'Ravi Shankar',
    gender: 'Male',
    age: '62Y',
    scheduledStartAt: minutesFromNow(-5),
    scheduledEndAt: minutesFromNow(10),
    patientWaitingSince: minutesFromNow(-4),
  }),
  session({
    id: id('03'),
    status: 'InProgress',
    appointmentNumber: 'AP2600447',
    patientName: 'Priya Kulkarni',
    uhid: 'NH26000131',
    scheduledStartAt: minutesFromNow(-15),
    scheduledEndAt: minutesFromNow(0),
    startedAt: minutesFromNow(-12),
  }),
  session({
    id: id('04'),
    status: 'Completed',
    appointmentNumber: 'AP2600440',
    patientName: 'Suresh Patil',
    uhid: 'NH26000140',
    gender: 'Male',
    age: '51Y',
    scheduledStartAt: minutesFromNow(-90),
    scheduledEndAt: minutesFromNow(-75),
    startedAt: minutesFromNow(-88),
    endedAt: minutesFromNow(-76),
    durationSeconds: 720,
    visitId: id('20'),
  }),
];

const DETAIL: TelehealthSessionDetail = {
  id: RECORD_ID,
  facilityId: FACILITY_ID,
  appointmentId: id('a1'),
  appointmentNumber: 'AP2600451',
  patientId: '0192a000-0000-7000-8000-000000000001',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
  phoneMasked: '98******10',
  practitionerId: STAFF_USER.practitionerId!,
  practitionerName: 'Dr. Meera Iyer',
  departmentId: '0192a000-0000-7000-8000-00000000de01',
  visitId: null,
  status: 'Scheduled',
  scheduledStartAt: minutesFromNow(20),
  scheduledEndAt: minutesFromNow(35),
  patientWaitingSince: null,
  startedAt: null,
  endedAt: null,
  durationSeconds: null,
  linkSentAt: minutesFromNow(-60),
  linkSendCount: 1,
  joinTokenExpiresAt: minutesFromNow(120),
  recordingConsent: false,
  recordingConsentAt: null,
  audioOnlyUsed: false,
  qualityNotes: null,
  cancelReason: null,
  messageCount: 1,
  participants: [],
  version: 3,
};

const MESSAGES: TelehealthMessageDto[] = [
  {
    id: id('21'),
    senderRole: 'Patient',
    senderName: 'Asha Devi',
    text: 'I have uploaded my sugar readings from last week.',
    sentAt: minutesFromNow(-30),
  },
];

const CONFIG: TelehealthConfig = {
  iceServers: [{ urls: ['stun:stun.nanahospitals.in:3478'], username: null, credential: null }],
  earlyJoinMinutes: 15,
  // Absolute: the test DOM cannot resolve a relative hub path (SignalR throws before the call can report it).
  hubPath: 'http://localhost/hubs/telehealth',
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/telehealth/config': CONFIG,
  'GET /api/v1/telehealth/sessions': SESSIONS,
  [`GET /api/v1/telehealth/sessions/${RECORD_ID}`]: DETAIL,
  [`GET /api/v1/telehealth/sessions/${RECORD_ID}/messages`]: MESSAGES,
};
