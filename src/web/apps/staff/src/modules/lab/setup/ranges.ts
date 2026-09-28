/**
 * Reference-interval editing helpers (pure; unit-tested). The server stores age bands in days; people think in days,
 * months and years, so the editor converts with the same factors as the seeded catalogue (a year is 365.25 days).
 */
import type { Gender } from '@hc360/core';
import type { ReferenceRangeDto } from '../api';

export type AgeUnit = 'days' | 'months' | 'years';

const DAYS_PER_YEAR = 365.25;
const DAYS_PER_MONTH = DAYS_PER_YEAR / 12;

/** Rounds half to even like .NET's Math.Round, so "18 years" is the same number of days as in the seeded catalogue. */
function roundHalfEven(value: number): number {
  const floor = Math.floor(value);
  const fraction = value - floor;
  if (Math.abs(fraction - 0.5) < 1e-9) {
    return floor % 2 === 0 ? floor : floor + 1;
  }
  return Math.round(value);
}

export function toDays(value: number, unit: AgeUnit): number {
  switch (unit) {
    case 'years':
      return roundHalfEven(value * DAYS_PER_YEAR);
    case 'months':
      return roundHalfEven(value * DAYS_PER_MONTH);
    default:
      return Math.round(value);
  }
}

/** The friendliest unit that expresses the number of days exactly (as produced by `toDays`). */
export function fromDays(days: number): { value: number; unit: AgeUnit } {
  if (days === 0) {
    return { value: 0, unit: 'years' };
  }
  const years = Math.round(days / DAYS_PER_YEAR);
  if (years > 0 && toDays(years, 'years') === days) {
    return { value: years, unit: 'years' };
  }
  const months = Math.round(days / DAYS_PER_MONTH);
  if (months > 0 && toDays(months, 'months') === days) {
    return { value: months, unit: 'months' };
  }
  return { value: days, unit: 'days' };
}

function describe(days: number): string {
  const { value, unit } = fromDays(days);
  const singular = unit.slice(0, -1);
  return `${value} ${value === 1 ? singular : unit}`;
}

/** "All ages", "18 years and above", "Below 1 month", "6 years to 12 years". */
export function ageBandLabel(fromDaysValue: number, toDaysValue: number | null): string {
  if (fromDaysValue === 0 && toDaysValue === null) {
    return 'All ages';
  }
  if (toDaysValue === null) {
    return `${describe(fromDaysValue)} and above`;
  }
  if (fromDaysValue === 0) {
    return `Below ${describe(toDaysValue)}`;
  }
  return `${describe(fromDaysValue)} to ${describe(toDaysValue)}`;
}

function formatNumber(value: number | null): string {
  return value === null ? '' : value.toLocaleString('en-IN', { maximumFractionDigits: 4 });
}

/** One-line summary of an interval for lists: "Male · 18 years and above · 13 – 17 · critical below 7, above 20". */
export function rangeSummary(range: ReferenceRangeDto): string {
  const who = range.gender === 'Male' || range.gender === 'Female' ? range.gender : 'Any sex';
  const limits =
    range.text ??
    (range.low !== null && range.high !== null
      ? `${formatNumber(range.low)} – ${formatNumber(range.high)}`
      : range.high !== null
        ? `up to ${formatNumber(range.high)}`
        : range.low !== null
          ? `${formatNumber(range.low)} and above`
          : (range.normalValues ?? []).join(' / ') || 'no limits');
  const critical = [
    range.criticalLow !== null ? `below ${formatNumber(range.criticalLow)}` : null,
    range.criticalHigh !== null ? `above ${formatNumber(range.criticalHigh)}` : null,
    ...(range.criticalValues ?? []),
  ].filter(Boolean);
  return [who, ageBandLabel(range.ageFromDays, range.ageToDays), limits, critical.length ? `critical ${critical.join(', ')}` : null].filter(Boolean).join(' · ');
}

/** "Negative, Non-reactive" → ["Negative", "Non-reactive"] (commas or new lines, blanks dropped, duplicates removed). */
export function parseList(text: string): string[] {
  const items = text
    .split(/[\n,]+/)
    .map((item) => item.trim())
    .filter(Boolean);
  const seen = new Set<string>();
  return items.filter((item) => {
    const key = item.toLowerCase();
    if (seen.has(key)) {
      return false;
    }
    seen.add(key);
    return true;
  });
}

/** The same consistency rules the server applies, so mistakes show on the row before saving. */
export function rangeProblem(range: ReferenceRangeDto): string | null {
  if (range.ageFromDays < 0 || (range.ageToDays !== null && range.ageToDays <= range.ageFromDays)) {
    return 'The age band must end after it starts.';
  }
  if (range.low !== null && range.high !== null && range.low > range.high) {
    return 'The lower limit cannot be above the upper limit.';
  }
  const floor = range.low ?? range.high;
  if (range.criticalLow !== null && floor !== null && range.criticalLow >= floor) {
    return 'The critical low limit must be below the reference interval.';
  }
  const ceiling = range.high ?? range.low;
  if (range.criticalHigh !== null && ceiling !== null && range.criticalHigh <= ceiling) {
    return 'The critical high limit must be above the reference interval.';
  }
  const normal = new Set((range.normalValues ?? []).map((v) => v.toLowerCase()));
  if ((range.criticalValues ?? []).some((v) => normal.has(v.toLowerCase()))) {
    return 'An answer cannot be both normal and critical.';
  }
  return null;
}

export function emptyRange(gender: Gender | null = null): ReferenceRangeDto {
  return {
    id: null,
    gender,
    ageFromDays: 0,
    ageToDays: null,
    low: null,
    high: null,
    criticalLow: null,
    criticalHigh: null,
    text: null,
    normalValues: null,
    criticalValues: null,
  };
}

/** Orders intervals the way people read them: by sex (any, male, female), then by age. */
export function sortRanges(ranges: readonly ReferenceRangeDto[]): ReferenceRangeDto[] {
  const rank = (gender: Gender | null) => (gender === 'Male' ? 1 : gender === 'Female' ? 2 : 0);
  return [...ranges].sort((a, b) => rank(a.gender) - rank(b.gender) || a.ageFromDays - b.ageFromDays);
}
