import { HOSPITAL_TIME_ZONE } from '@hc360/core';

const INPUT = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/;

/**
 * An instant as the value of an `<input type="datetime-local">` ("yyyy-MM-ddTHH:mm") in hospital time (IST), so the
 * time shown is the hospital's wall clock even on a workstation with a wrong time zone.
 */
export function toZonedInput(instant: Date, timeZone: string = HOSPITAL_TIME_ZONE): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(instant);
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? '00';
  return `${part('year')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}`;
}

/** Minutes the time zone is ahead of UTC at that instant (330 for IST). */
function offsetMinutes(instant: Date, timeZone: string): number {
  const match = INPUT.exec(toZonedInput(instant, timeZone));
  if (!match) {
    return 0;
  }
  const [, y, mo, d, h, mi] = match.map(Number);
  const wallClock = Date.UTC(y!, mo! - 1, d!, h!, mi!);
  const truncated = Math.floor(instant.getTime() / 60_000) * 60_000;
  return Math.round((wallClock - truncated) / 60_000);
}

/** The ISO instant for a datetime-local value entered in hospital time, or null when the value is incomplete. */
export function fromZonedInput(value: string, timeZone: string = HOSPITAL_TIME_ZONE): string | null {
  const match = INPUT.exec(value);
  if (!match) {
    return null;
  }
  const [, y, mo, d, h, mi] = match.map(Number);
  const guess = Date.UTC(y!, mo! - 1, d!, h!, mi!);
  const offset = offsetMinutes(new Date(guess), timeZone);
  return new Date(guess - offset * 60_000).toISOString();
}

/** True when the ISO instant lies more than `toleranceMinutes` after `now` (the server refuses such times). */
export function isInFuture(iso: string, now: Date, toleranceMinutes = 5): boolean {
  return Date.parse(iso) > now.getTime() + toleranceMinutes * 60_000;
}
