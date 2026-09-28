import type { Language } from './define';
import {
  formatCalendarDate,
  formatClockTime,
  formatDate,
  formatDateLong,
  formatDateTime,
  formatDayMonth,
  formatInr,
  formatMonthShort,
  formatMonthYear,
  formatNumber,
  formatTime,
  formatWeekday,
} from './format';
import { useLanguageStore } from './store';

export interface Formatters {
  language: Language;
  date(iso: string | null | undefined): string;
  dateLong(iso: string | null | undefined): string;
  time(iso: string | null | undefined): string;
  dateTime(iso: string | null | undefined): string;
  monthYear(iso: string | null | undefined): string;
  calendarDate(value: string | null | undefined): string;
  weekday(value: string, style?: 'short' | 'long'): string;
  dayMonth(value: string): string;
  monthShort(value: string): string;
  clock(value: string | null | undefined): string;
  inr(value: number | null | undefined, options?: { whole?: boolean }): string;
  number(value: number | null | undefined): string;
}

function build(language: Language): Formatters {
  return {
    language,
    date: (iso) => formatDate(iso, language),
    dateLong: (iso) => formatDateLong(iso, language),
    time: (iso) => formatTime(iso, language),
    dateTime: (iso) => formatDateTime(iso, language),
    monthYear: (iso) => formatMonthYear(iso, language),
    calendarDate: (value) => formatCalendarDate(value, language),
    weekday: (value, style) => formatWeekday(value, language, style),
    dayMonth: (value) => formatDayMonth(value, language),
    monthShort: (value) => formatMonthShort(value, language),
    clock: (value) => formatClockTime(value, language),
    inr: (value, options) => formatInr(value, language, options),
    number: (value) => formatNumber(value, language),
  };
}

const sets: Record<Language, Formatters> = { en: build('en'), hi: build('hi') };

/** Formatters bound to the current language: `const f = useFormat(); f.dateTime(appointment.startsAt)`. */
export function useFormat(): Formatters {
  return sets[useLanguageStore((state) => state.language)];
}

export function formattersFor(language: Language): Formatters {
  return sets[language];
}
