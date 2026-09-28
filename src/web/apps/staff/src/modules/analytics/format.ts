import { formatDateOnly, formatDateTime, formatInr } from '@hc360/core';
import type { CellValue, KpiUnit, KpiValue, ReportValueType } from './api';

/**
 * Display formatting for report values, KPI tiles and chart axes (pure, unit-tested). Money uses Indian grouping and
 * lakh/crore abbreviations, dates are IST calendar dates ("14 Sep 2026"), months "Sep 2026".
 */

const integer = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 0 });
const upToTwo = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2 });
const oneDecimal = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const upToOne = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 1 });
// Calendar dates carry no time zone: format them in UTC so the day never shifts (same month names as the rest of the app).
const monthFmt = new Intl.DateTimeFormat('en-IN', { month: 'short', year: 'numeric', timeZone: 'UTC' });
const shortDateFmt = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' });
const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})/;

/** "2026-09-14" → epoch milliseconds of that calendar day (UTC), or null. */
function calendarDay(value: string | null | undefined): number | null {
  const match = value ? ISO_DATE.exec(value) : null;
  return match ? Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])) : null;
}

const NUMERIC_TYPES: ReadonlySet<ReportValueType> = new Set(['Integer', 'Decimal', 'Money', 'Percent', 'Days', 'Minutes']);

/** Numbers are right-aligned in tables and can be charted. */
export function isNumericType(type: ReportValueType): boolean {
  return NUMERIC_TYPES.has(type);
}

/** A cell as a number (numbers, numeric strings); null for blanks and text. */
export function toNumber(value: CellValue | undefined): number | null {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? value : null;
  }
  if (typeof value === 'string' && value.trim() !== '' && !Number.isNaN(Number(value))) {
    return Number(value);
  }
  return null;
}

/** "2026-09-01" → "Sept 2026". */
export function formatMonth(value: string | null | undefined): string {
  const day = calendarDay(value);
  return day === null ? (value ?? '—') : monthFmt.format(day);
}

/** "2026-09-14" → "14 Sept" (chart axes, where the year is in the heading). */
export function formatShortDate(value: string | null | undefined): string {
  const day = calendarDay(value);
  return day === null ? (value ?? '—') : shortDateFmt.format(day);
}

/** "01 Sept 2026 – 15 Sept 2026", or one date when the range is a single day. */
export function describeRange(from: string | null | undefined, to: string | null | undefined): string {
  if (!from || !to) {
    return '—';
  }
  return from === to ? formatDateOnly(from) : `${formatDateOnly(from)} – ${formatDateOnly(to)}`;
}

/** Minutes as "42 min", "2 h 05 min" or "3 d 4 h" (turnaround times). */
export function formatMinutes(minutes: number): string {
  const sign = minutes < 0 ? '−' : '';
  const value = Math.abs(minutes);
  if (value < 120) {
    return `${sign}${upToOne.format(value)} min`;
  }
  const total = Math.round(value);
  if (total < 48 * 60) {
    const hours = Math.floor(total / 60);
    return `${sign}${hours} h ${String(total % 60).padStart(2, '0')} min`;
  }
  const days = Math.floor(total / 1440);
  return `${sign}${days} d ${Math.round((total % 1440) / 60)} h`;
}

/** ₹12.35 L / ₹1.2 Cr / ₹8.5 K: rupees abbreviated the Indian way (tiles, axes). */
export function formatInrCompact(value: number | null | undefined, digits = 2): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  const abs = Math.abs(value);
  const sign = value < 0 ? '−' : '';
  const fmt = new Intl.NumberFormat('en-IN', { maximumFractionDigits: digits });
  if (abs >= 1e7) {
    return `${sign}₹${fmt.format(abs / 1e7)} Cr`;
  }
  if (abs >= 1e5) {
    return `${sign}₹${fmt.format(abs / 1e5)} L`;
  }
  if (abs >= 1e3) {
    return `${sign}₹${new Intl.NumberFormat('en-IN', { maximumFractionDigits: 1 }).format(abs / 1e3)} K`;
  }
  return `${sign}₹${integer.format(abs)}`;
}

/**
 * A report value as shown in a table cell. Day columns already say "days" in their heading, so cells show the number;
 * headline figures (`figure`) carry the unit.
 */
export function formatValue(value: CellValue | undefined, type: ReportValueType, context: 'cell' | 'figure' = 'cell'): string {
  if (value === null || value === undefined || value === '') {
    return '—';
  }
  if (typeof value === 'boolean') {
    return value ? 'Yes' : 'No';
  }
  switch (type) {
    case 'Date':
      return typeof value === 'string' ? formatDateOnly(value.slice(0, 10)) : String(value);
    case 'DateTime':
      return typeof value === 'string' ? formatDateTime(value) : String(value);
    case 'Month':
      return typeof value === 'string' ? formatMonth(value) : String(value);
    case 'Text':
      return String(value);
    default:
      break;
  }

  const number = toNumber(value);
  if (number === null) {
    return String(value);
  }
  switch (type) {
    case 'Integer':
      return integer.format(number);
    case 'Decimal':
      return upToTwo.format(number);
    case 'Money':
      return formatInr(number);
    case 'Percent':
      return `${oneDecimal.format(number)}%`;
    case 'Days':
      return context === 'figure' ? `${oneDecimal.format(number)} ${Math.abs(number) === 1 ? 'day' : 'days'}` : oneDecimal.format(number);
    case 'Minutes':
      return formatMinutes(number);
    default:
      return String(value);
  }
}

/** Short axis/label text for a numeric value of a type (₹12 L, 45%, 30 min). */
export function formatAxisValue(value: number, type: ReportValueType): string {
  switch (type) {
    case 'Money':
      return formatInrCompact(value, 1);
    case 'Percent':
      return `${upToOne.format(value)}%`;
    case 'Minutes':
      return `${upToOne.format(value)} min`;
    case 'Days':
      return `${upToOne.format(value)} d`;
    default:
      return Math.abs(value) >= 1e5 ? formatInrCompact(value, 1).replace('₹', '') : upToTwo.format(value);
  }
}

/** A chart category as people read it: dates "14 Sep", months "Sep 2026", text as is. */
export function formatCategory(value: string, type: ReportValueType): string {
  switch (type) {
    case 'Date':
      return formatShortDate(value);
    case 'Month':
      return formatMonth(value);
    case 'DateTime':
      return formatDateTime(value);
    default:
      return value;
  }
}

// ------------------------------------------------------------------------------------------------ KPIs

/** The KPI value in its unit (₹ values abbreviated; the tile shows the full amount on hover). */
export function formatKpi(value: number | null | undefined, unit: KpiUnit): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  switch (unit) {
    case 'count':
      return integer.format(value);
    case 'percent':
      return `${oneDecimal.format(value)}%`;
    case 'days':
      return `${oneDecimal.format(value)} ${value === 1 ? 'day' : 'days'}`;
    case 'minutes':
      return formatMinutes(value);
    case 'inr':
      return formatInrCompact(value);
    case 'score':
      return `${value > 0 ? '+' : value < 0 ? '−' : ''}${upToOne.format(Math.abs(value))}`;
    default:
      return upToTwo.format(value);
  }
}

/** The full KPI value (₹ with paise) for tooltips and screen readers. */
export function formatKpiFull(value: number | null | undefined, unit: KpiUnit): string {
  return unit === 'inr' ? formatInr(value ?? null) : formatKpi(value, unit);
}

export type ChangeTone = 'good' | 'bad' | 'neutral';

export interface KpiChange {
  /** "+12.4%" or "+3.2 pts" (percentages and scores change in points, not percent of percent). */
  text: string;
  direction: 'up' | 'down' | 'flat';
  tone: ChangeTone;
  /** "Up 12.4% on the previous period, which is better" (for screen readers and tooltips). */
  description: string;
}

/** How a KPI moved against the previous period of the same length, and whether that is good. */
export function kpiChange(kpi: Pick<KpiValue, 'value' | 'previous' | 'changePercent' | 'higherIsBetter' | 'unit'>): KpiChange | null {
  if (kpi.value === null || kpi.previous === null) {
    return null;
  }
  const inPoints = kpi.unit === 'percent' || kpi.unit === 'score';
  const delta = inPoints ? kpi.value - kpi.previous : kpi.changePercent;
  if (delta === null || !Number.isFinite(delta)) {
    return null;
  }
  const rounded = Math.round(delta * 10) / 10;
  const direction = rounded > 0 ? 'up' : rounded < 0 ? 'down' : 'flat';
  const tone: ChangeTone = direction === 'flat' ? 'neutral' : (direction === 'up') === kpi.higherIsBetter ? 'good' : 'bad';
  const magnitude = `${upToOne.format(Math.abs(rounded))}${inPoints ? ' pts' : '%'}`;
  const text = direction === 'flat' ? `0${inPoints ? ' pts' : '%'}` : `${direction === 'up' ? '+' : '−'}${magnitude}`;
  const verdict = tone === 'good' ? ', which is better' : tone === 'bad' ? ', which is worse' : '';
  const description = direction === 'flat' ? 'No change on the previous period' : `${direction === 'up' ? 'Up' : 'Down'} ${magnitude} on the previous period${verdict}`;
  return { text, direction, tone, description };
}

/** "1.2 MB" for delivery files. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || bytes < 0) {
    return '—';
  }
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  if (bytes < 1024 * 1024) {
    return `${upToOne.format(bytes / 1024)} KB`;
  }
  return `${upToOne.format(bytes / (1024 * 1024))} MB`;
}

/** "1,234 rows" / "1 row". */
export function formatCount(count: number, singular: string, plural = `${singular}s`): string {
  return `${integer.format(count)} ${count === 1 ? singular : plural}`;
}
