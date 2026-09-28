import { HOSPITAL_TIME_ZONE } from '@hc360/core';

/**
 * Calendar and wall-clock helpers for scheduling screens. The server works on the hospital's IST calendar:
 * `DateOnly` arrives as "yyyy-MM-dd", `TimeOnly` as "HH:mm:ss" and instants as ISO strings with an offset. These
 * helpers never depend on the workstation's own time zone, so a laptop set to another zone still shows IST.
 */

/** A calendar date "yyyy-MM-dd" on the hospital calendar. */
export type IsoDate = string;

const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;
const TIME_PATTERN = /^(\d{1,2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?$/;
const DAY_MS = 86_400_000;

/** India Standard Time has no daylight saving, so the offset is fixed. */
export const IST_OFFSET = '+05:30';

function utcMs(date: IsoDate): number {
  const match = DATE_PATTERN.exec(date);
  if (!match) {
    return Number.NaN;
  }
  return Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
}

export function isIsoDate(value: unknown): value is IsoDate {
  if (typeof value !== 'string' || !DATE_PATTERN.test(value)) {
    return false;
  }
  const ms = utcMs(value);
  return !Number.isNaN(ms) && new Date(ms).toISOString().slice(0, 10) === value;
}

export function addDays(date: IsoDate, days: number): IsoDate {
  return new Date(utcMs(date) + days * DAY_MS).toISOString().slice(0, 10);
}

/** Whole days from `from` to `to` (negative when `to` is earlier). */
export function daysBetween(from: IsoDate, to: IsoDate): number {
  return Math.round((utcMs(to) - utcMs(from)) / DAY_MS);
}

/** ISO weekday: 1 = Monday … 7 = Sunday (the server's `Weekday` enum order). */
export function isoWeekday(date: IsoDate): number {
  const day = new Date(utcMs(date)).getUTCDay();
  return day === 0 ? 7 : day;
}

/** Monday of the week containing `date`. */
export function startOfWeek(date: IsoDate): IsoDate {
  return addDays(date, 1 - isoWeekday(date));
}

export function dateRange(from: IsoDate, count: number): IsoDate[] {
  return Array.from({ length: Math.max(0, count) }, (_, index) => addDays(from, index));
}

export function minDate(a: IsoDate, b: IsoDate): IsoDate {
  return a <= b ? a : b;
}

export function maxDate(a: IsoDate, b: IsoDate): IsoDate {
  return a >= b ? a : b;
}

const weekdayShortFormat = new Intl.DateTimeFormat('en-IN', { weekday: 'short', timeZone: 'UTC' });
const monthShortFormat = new Intl.DateTimeFormat('en-IN', { month: 'short', timeZone: 'UTC' });
const longFormat = new Intl.DateTimeFormat('en-IN', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
const mediumFormat = new Intl.DateTimeFormat('en-IN', { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
const shortFormat = new Intl.DateTimeFormat('en-IN', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' });

/** Pieces for a date chip: "Sat", 26, "Sept". */
export function dayParts(date: IsoDate): { weekday: string; day: number; month: string } {
  const ms = utcMs(date);
  return { weekday: weekdayShortFormat.format(ms), day: new Date(ms).getUTCDate(), month: monthShortFormat.format(ms) };
}

/** "Saturday, 26 September 2026". */
export function formatDayLong(date: IsoDate): string {
  return Number.isNaN(utcMs(date)) ? date : longFormat.format(utcMs(date));
}

/** "Sat, 26 Sept 2026". */
export function formatDayMedium(date: IsoDate): string {
  return Number.isNaN(utcMs(date)) ? date : mediumFormat.format(utcMs(date));
}

/** "Sat, 26 Sept". */
export function formatDayShort(date: IsoDate): string {
  return Number.isNaN(utcMs(date)) ? date : shortFormat.format(utcMs(date));
}

/** "Today", "Tomorrow", "Yesterday" or the short date. */
export function relativeDayLabel(date: IsoDate, today: IsoDate): string {
  const diff = daysBetween(today, date);
  if (diff === 0) {
    return 'Today';
  }
  if (diff === 1) {
    return 'Tomorrow';
  }
  if (diff === -1) {
    return 'Yesterday';
  }
  return formatDayShort(date);
}

const istPartsFormat = new Intl.DateTimeFormat('en-GB', {
  timeZone: HOSPITAL_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

/** The IST calendar date and minute of the day of an instant. */
export function istDateTime(iso: string): { date: IsoDate; minutes: number } {
  const parts = istPartsFormat.formatToParts(new Date(iso));
  const get = (type: Intl.DateTimeFormatPartTypes) => parts.find((part) => part.type === type)?.value ?? '0';
  const hour = Number(get('hour')) % 24;
  return { date: `${get('year')}-${get('month')}-${get('day')}`, minutes: hour * 60 + Number(get('minute')) };
}

export function istDate(iso: string): IsoDate {
  return istDateTime(iso).date;
}

/** "09:30:00" or "9:30" → 570; NaN when the text is not a time. */
export function timeToMinutes(time: string | null | undefined): number {
  const match = TIME_PATTERN.exec(time?.trim() ?? '');
  if (!match) {
    return Number.NaN;
  }
  const hours = Number(match[1]);
  const minutes = Number(match[2]);
  return hours > 23 || minutes > 59 ? Number.NaN : hours * 60 + minutes;
}

/** 570 → "09:30" (wraps within one day). */
export function minutesToTime(minutes: number): string {
  const total = ((Math.round(minutes) % 1440) + 1440) % 1440;
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
}

/** Wall-clock time in the app's style: "09:30:00" → "9:30 am", "13:05" → "1:05 pm". */
export function formatClock(time: string | null | undefined): string {
  const minutes = timeToMinutes(time);
  return Number.isNaN(minutes) ? '—' : formatMinutes(minutes);
}

export function formatMinutes(minutes: number): string {
  const total = ((Math.round(minutes) % 1440) + 1440) % 1440;
  const hours = Math.floor(total / 60);
  const hour12 = hours % 12 === 0 ? 12 : hours % 12;
  return `${hour12}:${String(total % 60).padStart(2, '0')} ${hours < 12 ? 'am' : 'pm'}`;
}

/** Short hour label for calendar gutters: 540 → "9 am", 780 → "1 pm". */
export function formatHour(minutes: number): string {
  const hours = Math.floor((((minutes % 1440) + 1440) % 1440) / 60);
  const hour12 = hours % 12 === 0 ? 12 : hours % 12;
  return `${hour12} ${hours < 12 ? 'am' : 'pm'}`;
}

/** Value for `<input type="time">` → the server's `TimeOnly` ("09:30" → "09:30:00"). */
export function toServerTime(value: string): string {
  const minutes = timeToMinutes(value);
  return Number.isNaN(minutes) ? value : `${minutesToTime(minutes)}:00`;
}

/** The server's `TimeOnly` → value for `<input type="time">` ("09:30:00" → "09:30"). */
export function toInputTime(value: string | null | undefined): string {
  const minutes = timeToMinutes(value);
  return Number.isNaN(minutes) ? '' : minutesToTime(minutes);
}

/** `<input type="datetime-local">` value, read as IST → ISO instant ("2026-09-26T10:30" → "2026-09-26T10:30:00+05:30"). */
export function istLocalToIso(local: string): string | null {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})$/.exec(local.trim());
  return match && isIsoDate(match[1]) && !Number.isNaN(timeToMinutes(match[2])) ? `${match[1]}T${match[2]}:00${IST_OFFSET}` : null;
}

/** ISO instant → `<input type="datetime-local">` value in IST. */
export function isoToIstLocal(iso: string): string {
  const { date, minutes } = istDateTime(iso);
  return `${date}T${minutesToTime(minutes)}`;
}

/** Whole minutes from `iso` until `now` (never negative). */
export function minutesSince(iso: string | null | undefined, now = Date.now()): number {
  if (!iso) {
    return 0;
  }
  return Math.max(0, Math.floor((now - Date.parse(iso)) / 60_000));
}

/** "45 min", "1 h 5 min". */
export function formatDuration(totalMinutes: number): string {
  const minutes = Math.max(0, Math.round(totalMinutes));
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest ? `${hours} h ${rest} min` : `${hours} h`;
}
