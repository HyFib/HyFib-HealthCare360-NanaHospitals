import { HOSPITAL_TIME_ZONE } from '@hc360/core';
import { LOCALES, type Language } from './define';

/**
 * Language-aware formatting for the portal (the shared `@hc360/core` formatters are English-only). Instants are
 * shown in hospital time (IST) whatever the phone's own time zone is, so "10:30 am" always means the clinic's clock.
 * Hindi uses Latin digits (hi-IN default), which is what Indian patients read on bills and SMS messages.
 */

type Formatters = {
  date: Intl.DateTimeFormat;
  dateLong: Intl.DateTimeFormat;
  time: Intl.DateTimeFormat;
  dateTime: Intl.DateTimeFormat;
  weekdayShort: Intl.DateTimeFormat;
  weekdayLong: Intl.DateTimeFormat;
  dayMonth: Intl.DateTimeFormat;
  monthYear: Intl.DateTimeFormat;
  calendarDate: Intl.DateTimeFormat;
  clock: Intl.DateTimeFormat;
  inr: Intl.NumberFormat;
  inrWhole: Intl.NumberFormat;
  number: Intl.NumberFormat;
};

const cache = new Map<Language, Formatters>();

function formatters(language: Language): Formatters {
  const cached = cache.get(language);
  if (cached) {
    return cached;
  }
  const locale = LOCALES[language];
  const zone = { timeZone: HOSPITAL_TIME_ZONE } as const;
  const created: Formatters = {
    date: new Intl.DateTimeFormat(locale, { ...zone, day: 'numeric', month: 'short', year: 'numeric' }),
    dateLong: new Intl.DateTimeFormat(locale, { ...zone, weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }),
    time: new Intl.DateTimeFormat(locale, { ...zone, hour: 'numeric', minute: '2-digit', hour12: true }),
    dateTime: new Intl.DateTimeFormat(locale, { ...zone, day: 'numeric', month: 'short', year: 'numeric', hour: 'numeric', minute: '2-digit', hour12: true }),
    // Calendar dates ("yyyy-MM-dd") carry no time zone: they are formatted as UTC midnight so the day never shifts.
    weekdayShort: new Intl.DateTimeFormat(locale, { timeZone: 'UTC', weekday: 'short' }),
    weekdayLong: new Intl.DateTimeFormat(locale, { timeZone: 'UTC', weekday: 'long' }),
    dayMonth: new Intl.DateTimeFormat(locale, { timeZone: 'UTC', day: 'numeric', month: 'short' }),
    monthYear: new Intl.DateTimeFormat(locale, { ...zone, month: 'long', year: 'numeric' }),
    calendarDate: new Intl.DateTimeFormat(locale, { timeZone: 'UTC', day: 'numeric', month: 'short', year: 'numeric' }),
    clock: new Intl.DateTimeFormat(locale, { timeZone: 'UTC', hour: 'numeric', minute: '2-digit', hour12: true }),
    inr: new Intl.NumberFormat(locale, { style: 'currency', currency: 'INR', minimumFractionDigits: 2, maximumFractionDigits: 2 }),
    inrWhole: new Intl.NumberFormat(locale, { style: 'currency', currency: 'INR', maximumFractionDigits: 0 }),
    number: new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }),
  };
  cache.set(language, created);
  return created;
}

const DASH = '—';

function instant(iso: string | null | undefined): Date | null {
  if (!iso) {
    return null;
  }
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** "yyyy-MM-dd" → UTC midnight of that calendar day, or null when malformed. */
export function parseCalendarDate(value: string | null | undefined): Date | null {
  const match = value ? /^(\d{4})-(\d{2})-(\d{2})$/.exec(value) : null;
  if (!match) {
    return null;
  }
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day ? date : null;
}

/** 26 Sept 2026 (instant, IST). */
export function formatDate(iso: string | null | undefined, language: Language): string {
  const date = instant(iso);
  return date ? formatters(language).date.format(date) : DASH;
}

/** Saturday, 26 September 2026 (instant, IST). */
export function formatDateLong(iso: string | null | undefined, language: Language): string {
  const date = instant(iso);
  return date ? formatters(language).dateLong.format(date) : DASH;
}

/** 10:30 am (instant, IST). */
export function formatTime(iso: string | null | undefined, language: Language): string {
  const date = instant(iso);
  return date ? formatters(language).time.format(date) : DASH;
}

/** 26 Sept 2026, 10:30 am (instant, IST). */
export function formatDateTime(iso: string | null | undefined, language: Language): string {
  const date = instant(iso);
  return date ? formatters(language).dateTime.format(date) : DASH;
}

/** September 2026 (instant, IST) — used for month headings in long lists. */
export function formatMonthYear(iso: string | null | undefined, language: Language): string {
  const date = instant(iso);
  return date ? formatters(language).monthYear.format(date) : DASH;
}

/** Calendar date "yyyy-MM-dd" → 14 Mar 1987. */
export function formatCalendarDate(value: string | null | undefined, language: Language): string {
  const date = parseCalendarDate(value);
  return date ? formatters(language).calendarDate.format(date) : value || DASH;
}

export function formatWeekday(value: string, language: Language, style: 'short' | 'long' = 'short'): string {
  const date = parseCalendarDate(value);
  return date ? (style === 'short' ? formatters(language).weekdayShort : formatters(language).weekdayLong).format(date) : value;
}

/** Calendar date → "26 Sept". */
export function formatDayMonth(value: string, language: Language): string {
  const date = parseCalendarDate(value);
  return date ? formatters(language).dayMonth.format(date) : value;
}

/** Wall-clock "HH:mm[:ss]" (a TimeOnly from the server) → "9:15 am". */
export function formatClockTime(value: string | null | undefined, language: Language): string {
  const match = value ? /^(\d{1,2}):(\d{2})/.exec(value) : null;
  if (!match) {
    return value || DASH;
  }
  const date = new Date(Date.UTC(2000, 0, 1, Number(match[1]), Number(match[2])));
  return formatters(language).clock.format(date);
}

/** ₹1,23,456.00 (Indian digit grouping). */
export function formatInr(value: number | null | undefined, language: Language, options?: { whole?: boolean }): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return DASH;
  }
  const f = formatters(language);
  return (options?.whole ? f.inrWhole : f.inr).format(value);
}

export function formatNumber(value: number | null | undefined, language: Language): string {
  return value === null || value === undefined || Number.isNaN(value) ? DASH : formatters(language).number.format(value);
}

const istDate = new Intl.DateTimeFormat('en-CA', { timeZone: HOSPITAL_TIME_ZONE, year: 'numeric', month: '2-digit', day: '2-digit' });

/** The IST calendar date ("yyyy-MM-dd") of an instant. */
export function istDateOf(value: string | number | Date): string {
  return istDate.format(value instanceof Date ? value : new Date(value));
}

/** Adds days to a calendar date ("yyyy-MM-dd"). */
export function addDays(value: string, days: number): string {
  const date = parseCalendarDate(value);
  if (!date) {
    return value;
  }
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

/** Whole days from `from` to `to` (calendar dates). */
export function daysBetween(from: string, to: string): number {
  const a = parseCalendarDate(from);
  const b = parseCalendarDate(to);
  return a && b ? Math.round((b.getTime() - a.getTime()) / 86_400_000) : 0;
}

/** "04:59" for a countdown; hours are added when needed ("1:05:00"). Never negative. */
export function formatCountdown(milliseconds: number): string {
  const total = Math.max(0, Math.ceil(milliseconds / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  const mm = String(minutes).padStart(2, '0');
  const ss = String(seconds).padStart(2, '0');
  return hours > 0 ? `${hours}:${mm}:${ss}` : `${mm}:${ss}`;
}

/** First name for greetings ("Dr." and similar titles are skipped). */
export function firstName(fullName: string | null | undefined): string {
  const parts = (fullName ?? '').replace(/^(dr|mr|mrs|ms|master|baby|smt|shri|kumari)\.?\s+/i, '').trim().split(/\s+/);
  return parts[0] ?? '';
}
