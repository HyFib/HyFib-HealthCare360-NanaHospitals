import { HOSPITAL_TIME_ZONE } from '@hc360/core';

/**
 * Wall-clock helpers for the hospital time zone (IST, UTC+05:30, no daylight saving).
 *
 * Shared by the specialty-care modules (OT, dialysis, physiotherapy, blood bank). `<input type="datetime-local">`
 * works with a zone-less "yyyy-MM-ddTHH:mm"; these helpers read and write it as IST whatever the workstation's own
 * time zone is, so a booking made on a mis-configured tablet still lands at the right instant.
 */
export const IST_OFFSET = '+05:30';

const LOCAL_INPUT = /^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/;
const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;
const TIME_ONLY = /^(\d{2}):(\d{2})$/;

const partsFormat = new Intl.DateTimeFormat('en-GB', {
  timeZone: HOSPITAL_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

function istParts(instant: Date): { date: string; time: string } {
  const parts = partsFormat.formatToParts(instant);
  const get = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? '00';
  return { date: `${get('year')}-${get('month')}-${get('day')}`, time: `${get('hour')}:${get('minute')}` };
}

function toDate(value: string | Date): Date | null {
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

export function isDateOnly(value: unknown): value is string {
  return typeof value === 'string' && DATE_ONLY.test(value);
}

/** Instant → "yyyy-MM-ddTHH:mm" in IST, for a datetime-local input. Empty string when missing or invalid. */
export function toIstInput(value: string | Date | null | undefined): string {
  const date = value ? toDate(value) : null;
  if (!date) {
    return '';
  }
  const { date: day, time } = istParts(date);
  return `${day}T${time}`;
}

/** "yyyy-MM-ddTHH:mm" read as IST wall-clock time → ISO instant (UTC). Null when empty or malformed. */
export function fromIstInput(value: string | null | undefined): string | null {
  const match = value ? LOCAL_INPUT.exec(value.trim()) : null;
  if (!match) {
    return null;
  }
  const [, day, hour, minute, second] = match;
  const date = toDate(`${day}T${hour}:${minute}:${second ?? '00'}${IST_OFFSET}`);
  return date ? date.toISOString() : null;
}

/** Current time as a datetime-local value in IST (minutes precision). */
export function nowIstInput(now: Date = new Date()): string {
  return toIstInput(now);
}

/** IST calendar date ("yyyy-MM-dd") of an instant. */
export function istDate(value: string | Date): string {
  const date = toDate(value);
  return date ? istParts(date).date : '';
}

/** IST wall-clock time ("HH:mm") of an instant. */
export function istTime(value: string | Date): string {
  const date = toDate(value);
  return date ? istParts(date).time : '';
}

/** Combines an IST date and "HH:mm" into an ISO instant. */
export function istDateTimeToIso(date: string | null | undefined, time: string | null | undefined): string | null {
  if (!date || !DATE_ONLY.test(date) || !time || !TIME_ONLY.test(time)) {
    return null;
  }
  return fromIstInput(`${date}T${time}`);
}

/** Midnight (IST) at the start of a calendar date, as epoch milliseconds. */
export function istMidnight(date: string): number {
  return Date.parse(`${date}T00:00:00${IST_OFFSET}`);
}

/** Minutes from IST midnight of `date` to the instant (negative before that day, above 1440 after it). */
export function minutesIntoIstDay(value: string, date: string): number {
  return Math.round((Date.parse(value) - istMidnight(date)) / 60_000);
}

/** Adds (or subtracts) whole days to a "yyyy-MM-dd" date. */
export function addDays(date: string, days: number): string {
  const [y, m, d] = date.split('-').map(Number);
  const utc = new Date(Date.UTC(y ?? 1970, (m ?? 1) - 1, (d ?? 1) + days));
  return utc.toISOString().slice(0, 10);
}

/** ISO weekday of a calendar date: 1 = Monday … 7 = Sunday. */
export function isoWeekday(date: string): number {
  const [y, m, d] = date.split('-').map(Number);
  const day = new Date(Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1)).getUTCDay();
  return day === 0 ? 7 : day;
}

/** Monday of the week that contains `date` (the server's week view runs Monday–Sunday). */
export function startOfWeek(date: string): string {
  return addDays(date, 1 - isoWeekday(date));
}

/** Whole days from `from` to `to` (both "yyyy-MM-dd"). */
export function daysBetween(from: string, to: string): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000);
}

const dayLabelFormat = new Intl.DateTimeFormat('en-IN', {
  weekday: 'short',
  day: 'numeric',
  month: 'short',
  timeZone: 'UTC',
});

/** "Mon, 26 Sept" for a calendar date. */
export function dayLabel(date: string): string {
  const [y, m, d] = date.split('-').map(Number);
  return dayLabelFormat.format(Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1));
}

/** Whole minutes between two instants (b − a). */
export function minutesBetween(a: string, b: string): number {
  return Math.round((Date.parse(b) - Date.parse(a)) / 60_000);
}

/** ISO instant `minutes` after `value`. */
export function addMinutesIso(value: string, minutes: number): string {
  return new Date(Date.parse(value) + minutes * 60_000).toISOString();
}

/** 135 → "2 h 15 min", 45 → "45 min". */
export function formatDuration(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined || Number.isNaN(minutes)) {
    return '—';
  }
  const total = Math.max(0, Math.round(minutes));
  const hours = Math.floor(total / 60);
  const rest = total % 60;
  if (hours === 0) {
    return `${rest} min`;
  }
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

/** "9:05 am" style clock time for minutes since midnight (used on calendar gutters). */
export function formatClock(minutesSinceMidnight: number): string {
  const normalised = ((minutesSinceMidnight % 1440) + 1440) % 1440;
  const hours = Math.floor(normalised / 60);
  const minutes = normalised % 60;
  const suffix = hours < 12 ? 'am' : 'pm';
  const display = hours % 12 === 0 ? 12 : hours % 12;
  return minutes === 0 ? `${display} ${suffix}` : `${display}:${String(minutes).padStart(2, '0')} ${suffix}`;
}
