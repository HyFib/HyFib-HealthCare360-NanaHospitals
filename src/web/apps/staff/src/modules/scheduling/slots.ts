import type { ConsultationMode, PreferredSession, ScheduleExceptionDto, SlotDto } from './api';
import { timeToMinutes, type IsoDate } from './dates';

/**
 * Pure slot logic for the booking screens. Boundaries follow the server's waitlist sessions so "morning" means the
 * same thing everywhere: morning before 12:00, afternoon 12:00–16:00, evening from 16:00 (IST wall clock).
 */

export type PartOfDay = 'Morning' | 'Afternoon' | 'Evening';
export const PARTS_OF_DAY: readonly PartOfDay[] = ['Morning', 'Afternoon', 'Evening'];

const NOON = 12 * 60;
const FOUR_PM = 16 * 60;

export function partOfDay(localTime: string): PartOfDay {
  const minutes = timeToMinutes(localTime);
  if (Number.isNaN(minutes) || minutes < NOON) {
    return 'Morning';
  }
  return minutes < FOUR_PM ? 'Afternoon' : 'Evening';
}

export function inPreferredSession(localTime: string, session: PreferredSession): boolean {
  return session === 'Any' || partOfDay(localTime) === session;
}

/** How the patient will be seen. */
export type VisitMode = 'InPerson' | 'Telehealth';

/** Does a session offered in `offered` mode serve a `wanted` visit? Mirrors the server's `SlotEngine.Offers`. */
export function slotServesMode(offered: ConsultationMode, wanted: VisitMode): boolean {
  return offered === 'Both' || offered === wanted;
}

export function slotsForMode(slots: readonly SlotDto[], wanted: VisitMode): SlotDto[] {
  return slots.filter((slot) => slotServesMode(slot.mode, wanted));
}

/** Any slot in the list can be booked as a video consultation. */
export function offersVideo(slots: readonly SlotDto[]): boolean {
  return slots.some((slot) => slot.mode !== 'InPerson');
}

export type SlotState = 'open' | 'few' | 'full';

/** Group slots (several patients per slot) show "few left" when a quarter or less of the seats remain. */
export function slotState(slot: Pick<SlotDto, 'capacity' | 'available'>): SlotState {
  if (slot.available <= 0) {
    return 'full';
  }
  return slot.capacity > 1 && slot.available <= Math.max(1, Math.floor(slot.capacity / 4)) ? 'few' : 'open';
}

export interface SlotGroup {
  part: PartOfDay;
  slots: SlotDto[];
  free: number;
}

/** Morning, afternoon and evening groups (empty groups left out), each in time order. */
export function groupSlotsByPartOfDay(slots: readonly SlotDto[]): SlotGroup[] {
  const sorted = [...slots].sort((a, b) => Date.parse(a.startsAt) - Date.parse(b.startsAt));
  return PARTS_OF_DAY.map((part) => {
    const inPart = sorted.filter((slot) => partOfDay(slot.localStart) === part);
    return { part, slots: inPart, free: inPart.filter((slot) => slot.available > 0).length };
  }).filter((group) => group.slots.length > 0);
}

export interface DaySummary {
  date: IsoDate;
  /** Slots on the day (including full ones). */
  total: number;
  /** Slots with at least one free seat. */
  free: number;
}

export function summarizeDays(dates: readonly IsoDate[], slots: readonly SlotDto[]): DaySummary[] {
  const counts = new Map<string, { total: number; free: number }>();
  for (const slot of slots) {
    const entry = counts.get(slot.date) ?? { total: 0, free: 0 };
    entry.total += 1;
    entry.free += slot.available > 0 ? 1 : 0;
    counts.set(slot.date, entry);
  }
  return dates.map((date) => ({ date, total: counts.get(date)?.total ?? 0, free: counts.get(date)?.free ?? 0 }));
}

export function firstDayWithFreeSlots(days: readonly DaySummary[]): IsoDate | null {
  return days.find((day) => day.free > 0)?.date ?? null;
}

/** Total free slots of a doctor for one visit mode. */
export function countFree(slots: readonly SlotDto[], wanted: VisitMode): number {
  return slots.filter((slot) => slot.available > 0 && slotServesMode(slot.mode, wanted)).length;
}

export interface DayRestriction {
  kind: 'leave' | 'block';
  reason: string;
  allDay: boolean;
  /** IST wall clock "HH:mm:ss" when the restriction covers part of the day. */
  start: string | null;
  end: string | null;
}

/** Leave and blocked time of a doctor on one date (extra sessions add slots and are not restrictions). */
export function restrictionsOn(
  date: IsoDate,
  exceptions: readonly ScheduleExceptionDto[],
  practitionerId?: string | null,
): DayRestriction[] {
  return exceptions
    .filter(
      (e) =>
        (e.type === 'Leave' || e.type === 'Block') &&
        e.fromDate <= date &&
        e.toDate >= date &&
        (!practitionerId || e.practitionerId === practitionerId),
    )
    .map((e) => ({
      kind: e.type === 'Leave' ? ('leave' as const) : ('block' as const),
      reason: e.reason,
      allDay: e.isAllDay,
      start: e.isAllDay ? null : e.startTime,
      end: e.isAllDay ? null : e.endTime,
    }));
}

/** True when the doctor is on leave for the whole day. */
export function isOnLeaveAllDay(restrictions: readonly DayRestriction[]): boolean {
  return restrictions.some((r) => r.kind === 'leave' && r.allDay);
}

/** Same instant, whatever the ISO formatting (offsets, fractional seconds). */
export function sameInstant(a: string | null | undefined, b: string | null | undefined): boolean {
  return Boolean(a && b) && Date.parse(a!) === Date.parse(b!);
}
