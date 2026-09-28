import { addDays, daysBetween } from './time';

/** The census API accepts at most 93 days (IST calendar dates). */
export const MAX_CENSUS_DAYS = 93;

export type CensusPreset = 'today' | '7d' | '30d' | 'month';

export const CENSUS_PRESETS: { value: CensusPreset; label: string }[] = [
  { value: 'today', label: 'Today' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: 'month', label: 'This month' },
];

/** From/to dates of a preset, ending today (IST). */
export function presetRange(preset: CensusPreset, today: string): { from: string; to: string } {
  switch (preset) {
    case 'today':
      return { from: today, to: today };
    case '30d':
      return { from: addDays(today, -29), to: today };
    case 'month':
      return { from: `${today.slice(0, 8)}01`, to: today };
    default:
      return { from: addDays(today, -6), to: today };
  }
}

/** The preset a range matches, if any (to highlight the right button). */
export function matchPreset(from: string, to: string, today: string): CensusPreset | null {
  return CENSUS_PRESETS.find((p) => {
    const range = presetRange(p.value, today);
    return range.from === from && range.to === to;
  })?.value ?? null;
}

/**
 * Keeps a requested range valid for the API: neither date after `today` (passed in, so the result never depends on
 * the clock), from ≤ to, and at most 93 days (the start moves forward when the range is too long).
 */
export function clampRange(from: string, to: string, today: string): { from: string; to: string } {
  let end = to > today ? today : to;
  let start = from > today ? today : from;
  if (start > end) {
    [start, end] = [end, start];
  }
  if (daysBetween(start, end) + 1 > MAX_CENSUS_DAYS) {
    start = addDays(end, -(MAX_CENSUS_DAYS - 1));
  }
  return { from: start, to: end };
}

export interface DailyPoint {
  date: string;
  admissions: number;
  discharges: number;
  deaths: number;
  transfersIn: number;
  transfersOut: number;
  midnightCensus: number;
}

/** Period totals of transfers between wards (hospital-level census has none; sum the ward rows). */
export function sumTransfers(days: readonly DailyPoint[]): { transfersIn: number; transfersOut: number } {
  return days.reduce((sum, d) => ({ transfersIn: sum.transfersIn + d.transfersIn, transfersOut: sum.transfersOut + d.transfersOut }), { transfersIn: 0, transfersOut: 0 });
}

/** "2026-09-26" → "26 Sep" for chart axes (no time zone involved). */
export function shortDate(date: string): string {
  const [year, month, day] = date.split('-').map(Number);
  if (!year || !month || !day) {
    return date;
  }
  return new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(Date.UTC(year, month - 1, day));
}
