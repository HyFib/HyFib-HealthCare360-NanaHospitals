/**
 * Hospital-calendar helpers (IST, UTC+05:30, no daylight saving) shared by the in-patient, nursing, CSSD, dietary and
 * canteen screens. They mirror the server's `IndiaClock` and `NursingShifts` so a shift boundary, a duty date or a
 * `datetime-local` value means the same instant on every workstation, whatever the device's own time zone.
 */

export const IST_OFFSET_MINUTES = 330;
const IST_OFFSET = '+05:30';
const MINUTE = 60_000;
const DAY = 86_400_000;
const LOCAL_INPUT = /^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/;
const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;

type Instant = Date | string | number;

function toMs(value: Instant): number {
  return typeof value === 'number' ? value : value instanceof Date ? value.getTime() : Date.parse(value);
}

/** IST wall-clock parts of an instant, computed arithmetically (independent of the workstation zone). */
export function istParts(value: Instant): { date: string; hours: number; minutes: number } {
  const shifted = new Date(toMs(value) + IST_OFFSET_MINUTES * MINUTE);
  return { date: shifted.toISOString().slice(0, 10), hours: shifted.getUTCHours(), minutes: shifted.getUTCMinutes() };
}

/** Instant → IST calendar date "yyyy-MM-dd". */
export function istDate(value: Instant): string {
  return istParts(value).date;
}

/** Instant → "yyyy-MM-ddTHH:mm" in IST for `<input type="datetime-local">`; empty when missing or invalid. */
export function toIstInput(value: Instant | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(toMs(value))) {
    return '';
  }
  const { date, hours, minutes } = istParts(value);
  return `${date}T${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}`;
}

/** "yyyy-MM-ddTHH:mm" read as IST wall-clock time → ISO instant; null when empty or malformed. */
export function fromIstInput(value: string | null | undefined): string | null {
  const match = value ? LOCAL_INPUT.exec(value.trim()) : null;
  if (!match) {
    return null;
  }
  const [, day, hour, minute, second] = match;
  const ms = Date.parse(`${day}T${hour}:${minute}:${second ?? '00'}${IST_OFFSET}`);
  return Number.isNaN(ms) ? null : new Date(ms).toISOString();
}

/** UTC instant (ISO) of an IST wall-clock time on an IST date. */
export function atIst(date: string, hours: number, minutes = 0): string {
  const match = DATE_ONLY.exec(date);
  if (!match) {
    throw new RangeError(`Invalid date ${date}`);
  }
  const ms =
    Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]), hours, minutes) - IST_OFFSET_MINUTES * MINUTE;
  return new Date(ms).toISOString();
}

/** Adds calendar days to "yyyy-MM-dd". */
export function addDays(date: string, days: number): string {
  const match = DATE_ONLY.exec(date);
  if (!match) {
    return date;
  }
  return new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])) + days * DAY)
    .toISOString()
    .slice(0, 10);
}

/** Whole days from `from` to `to` ("yyyy-MM-dd"); negative when `to` is earlier. */
export function daysBetween(from: string, to: string): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / DAY);
}

export type NursingShift = 'Morning' | 'Evening' | 'Night';

export const NURSING_SHIFTS: readonly NursingShift[] = ['Morning', 'Evening', 'Night'];

export const SHIFT_LABEL: Record<NursingShift, string> = {
  Morning: 'Morning (8 am – 2 pm)',
  Evening: 'Evening (2 pm – 8 pm)',
  Night: 'Night (8 pm – 8 am)',
};

/** Nursing shift of an instant: Morning 08:00–14:00, Evening 14:00–20:00, Night 20:00–08:00 IST. */
export function shiftAt(value: Instant): NursingShift {
  const { hours } = istParts(value);
  return hours >= 8 && hours < 14 ? 'Morning' : hours >= 14 && hours < 20 ? 'Evening' : 'Night';
}

/** Duty date of an instant: the IST date, except that the hours before 08:00 belong to the previous night's sheet. */
export function dutyDate(value: Instant): string {
  const { date, hours } = istParts(value);
  return hours < 8 ? addDays(date, -1) : date;
}

/** Whole minutes elapsed since an instant (never negative). */
export function minutesSince(value: Instant, now: number = Date.now()): number {
  return Math.max(0, Math.floor((now - toMs(value)) / MINUTE));
}

/** "45 min", "2 h 5 min", "3 d 4 h" — compact waiting times for queues and tiles. */
export function formatDuration(minutes: number): string {
  const total = Math.max(0, Math.round(minutes));
  if (total < 60) {
    return `${total} min`;
  }
  if (total < 24 * 60) {
    const h = Math.floor(total / 60);
    const m = total % 60;
    return m ? `${h} h ${m} min` : `${h} h`;
  }
  const d = Math.floor(total / (24 * 60));
  const h = Math.floor((total % (24 * 60)) / 60);
  return h ? `${d} d ${h} h` : `${d} d`;
}

/** "08:00:00" (server TimeOnly) → "8:00 am". */
export function formatTimeOfDay(value: string | null | undefined): string {
  const match = value ? /^(\d{1,2}):(\d{2})/.exec(value) : null;
  if (!match) {
    return '—';
  }
  const hours = Number(match[1]);
  const suffix = hours < 12 ? 'am' : 'pm';
  const twelve = hours % 12 === 0 ? 12 : hours % 12;
  return `${twelve}:${match[2]} ${suffix}`;
}

/** Current month as "yyyy-MM" in IST (staff credit statements and payroll periods). */
export function istMonth(value: Instant = Date.now()): string {
  return istDate(value).slice(0, 7);
}

/** "yyyy-MM" → previous month "yyyy-MM". */
export function previousMonth(period: string): string {
  const [year, month] = period.split('-').map(Number);
  if (!year || !month) {
    return period;
  }
  const date = new Date(Date.UTC(year, month - 2, 1));
  return date.toISOString().slice(0, 7);
}
