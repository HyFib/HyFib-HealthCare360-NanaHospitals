/**
 * Calendar-date helpers for "yyyy-MM-dd" strings (the server's DateOnly) and IST conversions for date-time inputs.
 * Pure functions (no React, no Fluent) so they are unit-tested and shared by the HR, Quality, CRM and Feedback screens.
 */
import { HOSPITAL_TIME_ZONE, todayIso } from '@hc360/core';

const DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const DAY_MS = 86_400_000;

export const MONTH_NAMES = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
] as const;

export const WEEKDAY_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;

/** True for a real calendar date written as "yyyy-MM-dd" (rejects 2026-02-30). */
export function isIsoDate(value: unknown): value is string {
  if (typeof value !== 'string') {
    return false;
  }
  const match = DATE.exec(value);
  if (!match) {
    return false;
  }
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

function toUtc(iso: string): Date {
  const match = DATE.exec(iso);
  if (!match) {
    throw new RangeError(`Not a calendar date: ${iso}`);
  }
  return new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
}

function fromUtc(date: Date): string {
  return date.toISOString().slice(0, 10);
}

export function addDays(iso: string, days: number): string {
  return fromUtc(new Date(toUtc(iso).getTime() + days * DAY_MS));
}

/** Same day in another month, clamped to the month's last day (31 Jan + 1 month = 28/29 Feb). */
export function addMonths(iso: string, months: number): string {
  const date = toUtc(iso);
  const target = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + months, 1));
  const last = daysInMonth(target.getUTCFullYear(), target.getUTCMonth() + 1);
  target.setUTCDate(Math.min(date.getUTCDate(), last));
  return fromUtc(target);
}

/** Whole days from `from` to `to` (negative when `to` is earlier). */
export function diffDays(from: string, to: string): number {
  return Math.round((toUtc(to).getTime() - toUtc(from).getTime()) / DAY_MS);
}

/** 0 = Sunday … 6 = Saturday. */
export function weekdayOf(iso: string): number {
  return toUtc(iso).getUTCDay();
}

/** The Monday (default) on or before the date. */
export function startOfWeek(iso: string, weekStartsOn = 1): string {
  const offset = (weekdayOf(iso) - weekStartsOn + 7) % 7;
  return addDays(iso, -offset);
}

/** Every date from `from` to `to`, inclusive (empty when `to` is before `from`). */
export function datesBetween(from: string, to: string): string[] {
  const days = diffDays(from, to);
  return days < 0 ? [] : Array.from({ length: days + 1 }, (_, index) => addDays(from, index));
}

export function daysInMonth(year: number, month: number): number {
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

export function monthStart(year: number, month: number): string {
  return `${year}-${String(month).padStart(2, '0')}-01`;
}

export function monthEnd(year: number, month: number): string {
  return `${year}-${String(month).padStart(2, '0')}-${String(daysInMonth(year, month)).padStart(2, '0')}`;
}

/** "September 2026" (or "Sep 2026" when short). */
export function monthLabel(year: number, month: number, short = false): string {
  const name = MONTH_NAMES[month - 1] ?? String(month);
  return `${short ? name.slice(0, 3) : name} ${year}`;
}

/** "2026-09" → "Sep 2026"; anything else is returned unchanged. */
export function monthKeyLabel(key: string, short = true): string {
  const match = /^(\d{4})-(\d{2})$/.exec(key);
  return match ? monthLabel(Number(match[1]), Number(match[2]), short) : key;
}

/** "Mon 14" style header for grids. */
export function dayHeader(iso: string): { weekday: string; day: number; month: string } {
  const date = toUtc(iso);
  return {
    weekday: WEEKDAY_SHORT[date.getUTCDay()] ?? '',
    day: date.getUTCDate(),
    month: MONTH_NAMES[date.getUTCMonth()]?.slice(0, 3) ?? '',
  };
}

/** Year and month (1-12) of today in IST. */
export function currentMonth(): { year: number; month: number } {
  const [year, month] = todayIso().split('-').map(Number);
  return { year: year ?? 1970, month: month ?? 1 };
}

export function previousMonth(): { year: number; month: number } {
  const { year, month } = currentMonth();
  return month === 1 ? { year: year - 1, month: 12 } : { year, month: month - 1 };
}

/** "480" → "8 h", "485" → "8 h 5 min", "45" → "45 min", 0/null → "—". */
export function formatMinutes(total: number | null | undefined): string {
  if (!total || total <= 0) {
    return '—';
  }
  const hours = Math.floor(total / 60);
  const minutes = Math.round(total % 60);
  if (hours === 0) {
    return `${minutes} min`;
  }
  return minutes === 0 ? `${hours} h` : `${hours} h ${minutes} min`;
}

/** Server TimeOnly ("09:00:00" or "09:00") → "9:00 AM". */
export function formatTimeOfDay(value: string | null | undefined): string {
  if (!value) {
    return '—';
  }
  const [h, m] = value.split(':').map(Number);
  if (h === undefined || m === undefined || Number.isNaN(h) || Number.isNaN(m)) {
    return value;
  }
  const suffix = h >= 12 ? 'PM' : 'AM';
  const hour = h % 12 === 0 ? 12 : h % 12;
  return `${hour}:${String(m).padStart(2, '0')} ${suffix}`;
}

/** Server TimeOnly → value for `<input type="time">` ("HH:mm"). */
export function toTimeInput(value: string | null | undefined): string {
  return value ? value.slice(0, 5) : '';
}

/** `<input type="time">` value ("HH:mm") → server TimeOnly ("HH:mm:ss"); empty → null. */
export function fromTimeInput(value: string | null | undefined): string | null {
  if (!value) {
    return null;
  }
  return /^\d{2}:\d{2}$/.test(value) ? `${value}:00` : value;
}

const istParts = new Intl.DateTimeFormat('en-CA', {
  timeZone: HOSPITAL_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

/** Instant → value for `<input type="datetime-local">` in hospital time ("2026-09-26T14:05"). */
export function toIstInput(iso: string | null | undefined): string {
  if (!iso) {
    return '';
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return '';
  }
  const parts = Object.fromEntries(istParts.formatToParts(date).map((part) => [part.type, part.value]));
  return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`;
}

/**
 * `<input type="datetime-local">` value read as hospital time (IST, UTC+05:30 all year) → ISO instant with offset.
 * Workstations may run another time zone; the hospital clock is what staff mean.
 */
export function fromIstInput(value: string | null | undefined): string | null {
  if (!value || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$/.test(value)) {
    return null;
  }
  return `${value.length === 16 ? `${value}:00` : value}+05:30`;
}

/** Current hospital time for `<input type="datetime-local">`. */
export function nowIstInput(): string {
  return toIstInput(new Date().toISOString());
}

/** Whole days from today (IST) until the date: 0 today, negative when past. */
export function daysFromToday(iso: string | null | undefined): number | null {
  return iso && isIsoDate(iso) ? diffDays(todayIso(), iso) : null;
}

/** "Overdue by 3 days" / "Due today" / "Due in 5 days". */
export function dueLabel(days: number | null): string {
  if (days === null) {
    return '—';
  }
  if (days < 0) {
    return `Overdue by ${-days} day${days === -1 ? '' : 's'}`;
  }
  if (days === 0) {
    return 'Due today';
  }
  return `Due in ${days} day${days === 1 ? '' : 's'}`;
}

/** Years to offer in year pickers: a few back, one ahead. */
export function yearOptions(back = 4, ahead = 1): number[] {
  const { year } = currentMonth();
  return Array.from({ length: back + ahead + 1 }, (_, index) => year + ahead - index);
}
