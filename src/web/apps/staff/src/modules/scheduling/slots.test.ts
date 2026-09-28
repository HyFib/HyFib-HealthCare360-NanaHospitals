import { describe, expect, it } from 'vitest';
import type { ScheduleExceptionDto, SlotDto } from './api';
import {
  countFree,
  firstDayWithFreeSlots,
  groupSlotsByPartOfDay,
  inPreferredSession,
  isOnLeaveAllDay,
  offersVideo,
  partOfDay,
  restrictionsOn,
  sameInstant,
  slotServesMode,
  slotState,
  slotsForMode,
  summarizeDays,
} from './slots';

function slot(date: string, localStart: string, overrides: Partial<SlotDto> = {}): SlotDto {
  const startsAt = `${date}T${localStart}+05:30`;
  return {
    startsAt,
    endsAt: new Date(Date.parse(startsAt) + 15 * 60_000).toISOString(),
    date,
    localStart,
    localEnd: localStart,
    capacity: 1,
    booked: 0,
    available: 1,
    mode: 'InPerson',
    facilityId: 'f1',
    departmentId: 'd1',
    room: null,
    ...overrides,
  };
}

describe('parts of the day', () => {
  it('uses the waitlist boundaries: before noon, noon to 4 pm, from 4 pm', () => {
    expect(partOfDay('09:00:00')).toBe('Morning');
    expect(partOfDay('11:59:00')).toBe('Morning');
    expect(partOfDay('12:00:00')).toBe('Afternoon');
    expect(partOfDay('15:45:00')).toBe('Afternoon');
    expect(partOfDay('16:00:00')).toBe('Evening');
    expect(inPreferredSession('17:00:00', 'Evening')).toBe(true);
    expect(inPreferredSession('17:00:00', 'Morning')).toBe(false);
    expect(inPreferredSession('17:00:00', 'Any')).toBe(true);
  });

  it('groups slots in time order and leaves out empty groups', () => {
    const groups = groupSlotsByPartOfDay([
      slot('2026-09-26', '17:15:00'),
      slot('2026-09-26', '09:15:00', { available: 0, booked: 1 }),
      slot('2026-09-26', '09:00:00'),
    ]);
    expect(groups.map((g) => g.part)).toEqual(['Morning', 'Evening']);
    expect(groups[0]!.slots.map((s) => s.localStart)).toEqual(['09:00:00', '09:15:00']);
    expect(groups[0]!.free).toBe(1);
    expect(groups[1]!.free).toBe(1);
  });
});

describe('visit modes', () => {
  it('matches the server rule: a "Both" session serves either mode', () => {
    expect(slotServesMode('Both', 'Telehealth')).toBe(true);
    expect(slotServesMode('InPerson', 'Telehealth')).toBe(false);
    expect(slotServesMode('Telehealth', 'InPerson')).toBe(false);
    expect(slotServesMode('InPerson', 'InPerson')).toBe(true);
  });

  it('filters and counts slots for a mode', () => {
    const slots = [
      slot('2026-09-26', '09:00:00', { mode: 'Both' }),
      slot('2026-09-26', '09:15:00', { mode: 'InPerson' }),
      slot('2026-09-26', '09:30:00', { mode: 'Telehealth', available: 0, booked: 1 }),
    ];
    expect(slotsForMode(slots, 'Telehealth').map((s) => s.localStart)).toEqual(['09:00:00', '09:30:00']);
    expect(countFree(slots, 'Telehealth')).toBe(1);
    expect(countFree(slots, 'InPerson')).toBe(2);
    expect(offersVideo(slots)).toBe(true);
    expect(offersVideo([slots[1]!])).toBe(false);
  });
});

describe('slot state', () => {
  it('marks full slots and nearly full group slots', () => {
    expect(slotState({ capacity: 1, available: 1 })).toBe('open');
    expect(slotState({ capacity: 1, available: 0 })).toBe('full');
    expect(slotState({ capacity: 8, available: 2 })).toBe('few');
    expect(slotState({ capacity: 8, available: 3 })).toBe('open');
    expect(slotState({ capacity: 2, available: 1 })).toBe('few');
    expect(slotState({ capacity: 4, available: -1 })).toBe('full');
  });
});

describe('date strip', () => {
  it('counts slots per day and finds the first day with a free slot', () => {
    const days = summarizeDays(
      ['2026-09-26', '2026-09-27', '2026-09-28'],
      [
        slot('2026-09-26', '09:00:00', { available: 0, booked: 1 }),
        slot('2026-09-28', '09:00:00'),
        slot('2026-09-28', '09:15:00'),
      ],
    );
    expect(days).toEqual([
      { date: '2026-09-26', total: 1, free: 0 },
      { date: '2026-09-27', total: 0, free: 0 },
      { date: '2026-09-28', total: 2, free: 2 },
    ]);
    expect(firstDayWithFreeSlots(days)).toBe('2026-09-28');
    expect(firstDayWithFreeSlots(days.slice(0, 2))).toBeNull();
  });
});

describe('leave and blocked time', () => {
  const base: ScheduleExceptionDto = {
    id: 'e1',
    practitionerId: 'p1',
    practitionerName: 'Dr. Mehta',
    facilityId: null,
    type: 'Leave',
    fromDate: '2026-09-28',
    toDate: '2026-09-30',
    isAllDay: true,
    startTime: null,
    endTime: null,
    reason: 'Conference',
    departmentId: null,
    slotMinutes: null,
    capacityPerSlot: null,
    mode: null,
    room: null,
    version: 1,
  };

  it('finds leave and blocks on a date, ignoring extra sessions and other doctors', () => {
    const exceptions: ScheduleExceptionDto[] = [
      base,
      { ...base, id: 'e2', type: 'Block', isAllDay: false, startTime: '11:00:00', endTime: '13:00:00', fromDate: '2026-09-29', toDate: '2026-09-29', reason: 'Surgery list' },
      { ...base, id: 'e3', type: 'ExtraSession', fromDate: '2026-09-29', toDate: '2026-09-29' },
      { ...base, id: 'e4', practitionerId: 'p2' },
    ];
    const onDay = restrictionsOn('2026-09-29', exceptions, 'p1');
    expect(onDay).toEqual([
      { kind: 'leave', reason: 'Conference', allDay: true, start: null, end: null },
      { kind: 'block', reason: 'Surgery list', allDay: false, start: '11:00:00', end: '13:00:00' },
    ]);
    expect(isOnLeaveAllDay(onDay)).toBe(true);
    expect(restrictionsOn('2026-10-01', exceptions, 'p1')).toEqual([]);
    expect(isOnLeaveAllDay([{ kind: 'leave', reason: 'Half day', allDay: false, start: '14:00:00', end: '18:00:00' }])).toBe(false);
  });

  it('compares instants regardless of formatting', () => {
    expect(sameInstant('2026-09-26T03:30:00+00:00', '2026-09-26T09:00:00+05:30')).toBe(true);
    expect(sameInstant('2026-09-26T03:30:00Z', '2026-09-26T03:45:00Z')).toBe(false);
    expect(sameInstant(null, '2026-09-26T03:30:00Z')).toBe(false);
  });
});
