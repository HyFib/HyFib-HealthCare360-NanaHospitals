import type { ScheduleDto, WaitlistEntryDto } from '@/modules/scheduling/api';
import { PATIENTS } from '../accessibility/fixtures/frontOffice';
import { FACILITY_ID, paged, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Doctors' weekly OPD sessions (one of them inactive), and the waitlist: one patient waiting, one offered a freed slot
// (both can be booked or removed) and one already booked.

const now = Date.now();
const daysFromNow = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000074${suffix}`;
const GM = '0192a000-0000-7000-8000-00000000de01';
const ANIL = '0192a000-0000-7000-8000-0000000000d2';

const schedule = (overrides: Partial<ScheduleDto> & Pick<ScheduleDto, 'id' | 'weekday'>): ScheduleDto => ({
  facilityId: FACILITY_ID,
  practitionerId: STAFF_USER.practitionerId!,
  practitionerName: 'Dr. Meera Iyer',
  departmentId: GM,
  departmentName: 'General medicine',
  startTime: '09:00:00',
  endTime: '13:00:00',
  slotMinutes: 15,
  capacityPerSlot: 1,
  slotsPerSession: 16,
  mode: 'InPerson',
  room: 'Room 3',
  effectiveFrom: daysFromNow(-120),
  effectiveTo: null,
  isActive: true,
  notes: null,
  version: 1,
  ...overrides,
});

const SCHEDULES: ScheduleDto[] = [
  schedule({ id: RECORD_ID, weekday: 'Monday' }),
  schedule({ id: id('02'), weekday: 'Wednesday', mode: 'Both' }),
  schedule({ id: id('03'), weekday: 'Friday', startTime: '16:00:00', endTime: '19:00:00', slotsPerSession: 12 }),
  schedule({
    id: id('04'),
    weekday: 'Tuesday',
    practitionerId: ANIL,
    practitionerName: 'Dr. Anil Rao',
    room: 'Room 5',
    capacityPerSlot: 2,
    slotsPerSession: 32,
  }),
  schedule({
    id: id('05'),
    weekday: 'Saturday',
    practitionerId: ANIL,
    practitionerName: 'Dr. Anil Rao',
    room: 'Room 5',
    isActive: false,
    effectiveTo: daysFromNow(-10),
    notes: 'Saturday OPD stopped from October.',
  }),
];

const [ASHA, RAVI, KAMALA] = [PATIENTS[0]!, PATIENTS[1]!, PATIENTS[2]!];

const entry = (overrides: Partial<WaitlistEntryDto> & Pick<WaitlistEntryDto, 'id' | 'status'>): WaitlistEntryDto => ({
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  phoneMasked: ASHA.phoneMasked,
  practitionerId: STAFF_USER.practitionerId!,
  practitionerName: 'Dr. Meera Iyer',
  departmentId: GM,
  preferredFrom: daysFromNow(1),
  preferredTo: daysFromNow(7),
  preferredSession: 'Morning',
  isTelehealth: false,
  channel: 'Phone',
  notes: null,
  createdAt: minutesAgo(60 * 26),
  offeredAt: null,
  offeredSlotStartsAt: null,
  offerCount: 0,
  appointmentId: null,
  ...overrides,
});

const WAITLIST: WaitlistEntryDto[] = [
  entry({ id: RECORD_ID, status: 'Waiting', notes: 'Wants the first slot after 10 am.' }),
  entry({
    id: id('20'),
    status: 'Offered',
    patientId: RAVI.id,
    uhid: RAVI.uhid,
    patientName: RAVI.fullName,
    phoneMasked: RAVI.phoneMasked,
    preferredSession: 'Any',
    channel: 'WhatsApp',
    offeredAt: minutesAgo(25),
    offeredSlotStartsAt: `${daysFromNow(2)}T10:30:00+05:30`,
    offerCount: 1,
  }),
  entry({
    id: id('21'),
    status: 'Booked',
    patientId: KAMALA.id,
    uhid: KAMALA.uhid,
    patientName: KAMALA.fullName,
    phoneMasked: KAMALA.phoneMasked,
    practitionerId: ANIL,
    practitionerName: 'Dr. Anil Rao',
    channel: 'WalkIn',
    appointmentId: id('a3'),
  }),
];

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/scheduling/schedules': SCHEDULES,
  'GET /api/v1/scheduling/waitlist': paged(WAITLIST),
};
