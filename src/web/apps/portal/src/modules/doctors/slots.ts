import { addDays, daysBetween } from '@/i18n/format';
import type { PublicSlot, PublicSlotDay } from '@/lib/types';

/** Parts of the day, with the same boundaries the waiting list uses (IST wall clock). */
export const DAY_PARTS = ['morning', 'afternoon', 'evening'] as const;
export type DayPart = (typeof DAY_PARTS)[number];

/** How many days the date strip shows at once (one server call covers them; the API allows up to 14). */
export const STRIP_DAYS = 7;

/** "09:15:00" → morning (before 12:00), afternoon (12:00–15:59) or evening (16:00 onwards). */
export function dayPartOf(localTime: string): DayPart {
  const hour = Number(/^(\d{1,2})/.exec(localTime)?.[1] ?? 0);
  return hour < 12 ? 'morning' : hour < 16 ? 'afternoon' : 'evening';
}

/** Free slots of a day grouped by part of the day, in time order (empty parts are left out). */
export function groupByDayPart(slots: readonly PublicSlot[]): { part: DayPart; slots: PublicSlot[] }[] {
  const sorted = [...slots].filter((slot) => slot.seatsLeft > 0).sort((a, b) => a.startsAt.localeCompare(b.startsAt));
  return DAY_PARTS.map((part) => ({ part, slots: sorted.filter((slot) => dayPartOf(slot.localTime) === part) })).filter((group) => group.slots.length > 0);
}

/** The calendar days shown in the strip, starting at `start`, never past the booking window's last day. */
export function stripDays(start: string, lastDay: string, count = STRIP_DAYS): string[] {
  const days: string[] = [];
  for (let offset = 0; offset < count; offset++) {
    const day = addDays(start, offset);
    if (daysBetween(day, lastDay) < 0) {
      break;
    }
    days.push(day);
  }
  return days;
}

/** Free seats per day (0 for days without slots). */
export function freeSeatsByDay(days: readonly PublicSlotDay[] | undefined): Map<string, number> {
  const counts = new Map<string, number>();
  for (const day of days ?? []) {
    counts.set(
      day.date,
      day.slots.reduce((sum, slot) => sum + Math.max(0, slot.seatsLeft), 0),
    );
  }
  return counts;
}

/** The first day in `days` (in order) that still has a free seat, if any. */
export function firstFreeDay(days: readonly string[], counts: Map<string, number>): string | null {
  return days.find((day) => (counts.get(day) ?? 0) > 0) ?? null;
}

/** Keeps the chosen day if it is still in the strip and free; otherwise the first free day in view. */
export function chooseDay(selected: string | null, days: readonly string[], counts: Map<string, number>): string | null {
  if (selected && days.includes(selected) && (counts.get(selected) ?? 0) > 0) {
    return selected;
  }
  return firstFreeDay(days, counts);
}

/** Start of the strip page that contains `day` (pages of STRIP_DAYS from `today`). */
export function stripStartFor(day: string, today: string): string {
  const offset = Math.max(0, daysBetween(today, day));
  return addDays(today, Math.floor(offset / STRIP_DAYS) * STRIP_DAYS);
}
