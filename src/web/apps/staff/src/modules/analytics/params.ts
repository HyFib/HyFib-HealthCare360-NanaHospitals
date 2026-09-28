import type { ReportChartType, ReportDrillDown, ReportInputs, ReportParameter, ScheduleFrequency, Weekday } from './api';
import { CHART_TYPES, CUSTOM_PERIOD, DEFAULT_PERIOD, LAST_DAY_OF_MONTH, PAGE_SIZES, PERIOD_PRESETS } from './labels';

/**
 * Report inputs in the URL, their defaults, drill-down targets, period arithmetic (IST calendar, mirroring the server's
 * ReportPeriods) and schedule wording. Pure and unit-tested.
 */

/** Parameter keys are camelCase words ("departmentId"); anything else in the URL is ignored. */
const INPUT_KEY = /^[a-z][A-Za-z0-9]{0,39}$/;
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;
export const MAX_INPUT_LENGTH = 64;
export const MAX_INPUTS = 30;

/** URL keys of the report screen that are not report inputs. */
export const REPORT_CONTROL_KEYS: ReadonlySet<string> = new Set(['page', 'size', 'sort', 'desc', 'view']);

/** The report screen's search params: the report inputs (flat) plus paging, sorting and the chart view. */
export interface ReportSearch {
  [key: string]: string | number | boolean | undefined;
  page?: number;
  size?: number;
  sort?: string;
  desc?: boolean;
  view?: ReportChartType;
}

/** Report inputs from untrusted search params: camelCase keys, short scalar values, at most 30. */
export function sanitizeInputs(raw: Record<string, unknown> | null | undefined, exclude: ReadonlySet<string> = REPORT_CONTROL_KEYS): ReportInputs {
  const inputs: ReportInputs = {};
  let count = 0;
  for (const [key, value] of Object.entries(raw ?? {})) {
    if (count >= MAX_INPUTS || exclude.has(key) || !INPUT_KEY.test(key)) {
      continue;
    }
    const text =
      typeof value === 'string' ? value.trim() : typeof value === 'number' && Number.isFinite(value) ? String(value) : typeof value === 'boolean' ? String(value) : '';
    if (text === '' || text.length > MAX_INPUT_LENGTH) {
      continue;
    }
    inputs[key] = text;
    count++;
  }
  return inputs;
}

/** Whole numbers go into the URL as numbers so they read `?days=90`, not `?days="90"`. */
export function toSearchInputs(inputs: ReportInputs): Record<string, string | number> {
  const search: Record<string, string | number> = {};
  for (const [key, value] of Object.entries(inputs)) {
    search[key] = /^-?\d{1,9}$/.test(value) ? Number(value) : value;
  }
  return search;
}

export function parseReportSearch(raw: Record<string, unknown>): ReportSearch {
  const search: ReportSearch = { ...toSearchInputs(sanitizeInputs(raw)) };
  if (typeof raw.page === 'number' && Number.isInteger(raw.page) && raw.page > 1 && raw.page <= 10_000) {
    search.page = raw.page;
  }
  if (typeof raw.size === 'number' && PAGE_SIZES.includes(raw.size)) {
    search.size = raw.size;
  }
  if (typeof raw.sort === 'string' && INPUT_KEY.test(raw.sort)) {
    search.sort = raw.sort;
  }
  if (typeof raw.desc === 'boolean') {
    search.desc = raw.desc;
  }
  if (typeof raw.view === 'string' && CHART_TYPES.includes(raw.view as ReportChartType)) {
    search.view = raw.view as ReportChartType;
  }
  return search;
}

/** The inputs part of the report screen's search params. */
export function inputsOf(search: ReportSearch): ReportInputs {
  return sanitizeInputs(search);
}

/**
 * The inputs the report form shows: the declared parameters only, with the report's defaults filled in (the server
 * applies the same defaults, so what the form shows is what ran).
 */
export function withDefaults(parameters: readonly ReportParameter[], inputs: ReportInputs): ReportInputs {
  const result: ReportInputs = {};
  for (const parameter of parameters) {
    if (parameter.kind === 'Period') {
      const hasDates = Boolean(inputs.from || inputs.to);
      const preset = inputs.period ?? (hasDates ? CUSTOM_PERIOD : (parameter.defaultValue ?? DEFAULT_PERIOD));
      result.period = preset;
      if (preset === CUSTOM_PERIOD) {
        if (inputs.from) {
          result.from = inputs.from;
        }
        if (inputs.to) {
          result.to = inputs.to;
        }
      }
      continue;
    }
    const value = inputs[parameter.key] ?? (parameter.kind === 'Choice' || parameter.kind === 'Number' ? (parameter.defaultValue ?? undefined) : undefined);
    if (value !== undefined && value !== '') {
      result[parameter.key] = value;
    }
  }
  return result;
}

/** Sets (or clears) one input. Choosing a named period drops custom dates. */
export function setInput(current: ReportInputs, key: string, value: string | null | undefined): ReportInputs {
  const next: ReportInputs = { ...current };
  if (value === null || value === undefined || value === '') {
    delete next[key];
  } else {
    next[key] = value;
  }
  if (key === 'period' && value !== CUSTOM_PERIOD) {
    delete next.from;
    delete next.to;
  }
  return next;
}

/** The report a row drills into: same period and facility, the row's key, and the drill's fixed inputs. */
export function drillInputs(current: ReportInputs, drill: ReportDrillDown, key: string): ReportInputs {
  const next: ReportInputs = {};
  for (const shared of ['period', 'from', 'to', 'facilityId']) {
    const value = current[shared];
    if (value) {
      next[shared] = value;
    }
  }
  Object.assign(next, drill.parameters ?? {});
  next[drill.parameterKey] = key;
  return next;
}

/** Inputs a scheduled e-mail may keep: never a facility (schedules run for their own) or fixed dates. */
export function scheduleInputs(inputs: ReportInputs): ReportInputs {
  const next: ReportInputs = { ...inputs };
  delete next.facilityId;
  if (next.period === CUSTOM_PERIOD) {
    delete next.period;
  }
  delete next.from;
  delete next.to;
  return next;
}

/** A dashboard-wide period ("show every widget for last month") applied to a widget's saved inputs. */
export function applyPeriodOverride(parameters: readonly ReportParameter[] | undefined, inputs: ReportInputs, override: string | undefined): ReportInputs {
  if (!override || !parameters?.some((p) => p.kind === 'Period')) {
    return inputs;
  }
  const next: ReportInputs = { ...inputs, period: override };
  delete next.from;
  delete next.to;
  return next;
}

// ------------------------------------------------------------------------------------------------ periods (IST calendar dates)

function parts(iso: string): [number, number, number] {
  const [y, m, d] = iso.split('-').map(Number);
  return [y ?? 1970, m ?? 1, d ?? 1];
}

function toIso(epochMs: number): string {
  return new Date(epochMs).toISOString().slice(0, 10);
}

function utc(y: number, m: number, d: number): number {
  return Date.UTC(y, m - 1, d);
}

export function isIsoDate(value: string | null | undefined): value is string {
  return typeof value === 'string' && ISO_DATE.test(value) && !Number.isNaN(Date.parse(value));
}

export function addDays(iso: string, days: number): string {
  const [y, m, d] = parts(iso);
  return toIso(utc(y, m, d) + days * 86_400_000);
}

/** Inclusive day count of a date range ("2026-09-01".."2026-09-30" → 30). */
export function daysInRange(from: string, to: string): number {
  const [fy, fm, fd] = parts(from);
  const [ty, tm, td] = parts(to);
  return Math.round((utc(ty, tm, td) - utc(fy, fm, fd)) / 86_400_000) + 1;
}

export function startOfWeek(iso: string): string {
  const [y, m, d] = parts(iso);
  const weekday = new Date(utc(y, m, d)).getUTCDay(); // 0 = Sunday
  return addDays(iso, -((weekday + 6) % 7));
}

function firstOfMonth(y: number, m: number): string {
  return toIso(utc(y, m, 1));
}

/** Resolves a named period to dates relative to `today` (IST, "yyyy-MM-dd"); null for custom or unknown presets. */
export function resolvePeriod(preset: string, today: string): { from: string; to: string } | null {
  const [y, m] = parts(today);
  switch (preset) {
    case 'today':
      return { from: today, to: today };
    case 'yesterday':
      return { from: addDays(today, -1), to: addDays(today, -1) };
    case 'last7Days':
      return { from: addDays(today, -6), to: today };
    case 'last30Days':
      return { from: addDays(today, -29), to: today };
    case 'thisWeek':
      return { from: startOfWeek(today), to: today };
    case 'lastWeek': {
      const from = addDays(startOfWeek(today), -7);
      return { from, to: addDays(from, 6) };
    }
    case 'thisMonth':
      return { from: firstOfMonth(y, m), to: today };
    case 'lastMonth':
      return { from: firstOfMonth(y, m - 1), to: addDays(firstOfMonth(y, m), -1) };
    case 'thisQuarter':
      return { from: firstOfMonth(y, Math.floor((m - 1) / 3) * 3 + 1), to: today };
    case 'lastQuarter': {
      const quarterStart = Math.floor((m - 1) / 3) * 3 + 1;
      return { from: firstOfMonth(y, quarterStart - 3), to: addDays(firstOfMonth(y, quarterStart), -1) };
    }
    case 'thisFinancialYear':
      return { from: firstOfMonth(m >= 4 ? y : y - 1, 4), to: today };
    case 'lastFinancialYear': {
      const start = m >= 4 ? y : y - 1;
      return { from: firstOfMonth(start - 1, 4), to: addDays(firstOfMonth(start, 4), -1) };
    }
    default:
      return null;
  }
}

/** The period of the same length that ends the day before `from` (the KPI comparison period). */
export function previousRange(from: string, to: string): { from: string; to: string } {
  const length = Math.max(1, daysInRange(from, to));
  const previousTo = addDays(from, -1);
  return { from: addDays(previousTo, -(length - 1)), to: previousTo };
}

export function periodLabel(value: string | null | undefined, presets: readonly { value: string; label: string }[] = PERIOD_PRESETS): string {
  return presets.find((p) => p.value === value)?.label ?? 'Custom dates';
}

export function isRelativePeriod(value: string | null | undefined): boolean {
  return Boolean(value) && value !== CUSTOM_PERIOD && PERIOD_PRESETS.some((p) => p.value === value);
}

/** The complete period that just ended: what a daily, weekly or monthly e-mail covers by default. */
export function defaultPeriodFor(frequency: ScheduleFrequency): string {
  return frequency === 'Daily' ? 'yesterday' : frequency === 'Weekly' ? 'lastWeek' : 'lastMonth';
}

// ------------------------------------------------------------------------------------------------ schedules

export function ordinal(n: number): string {
  const tens = n % 100;
  if (tens >= 11 && tens <= 13) {
    return `${n}th`;
  }
  switch (n % 10) {
    case 1:
      return `${n}st`;
    case 2:
      return `${n}nd`;
    case 3:
      return `${n}rd`;
    default:
      return `${n}th`;
  }
}

/** "Every Monday at 07:00 IST" — the same wording as the server's schedule summary. */
export function describeSchedule(frequency: ScheduleFrequency, time: string, dayOfWeek: Weekday | null, dayOfMonth: number | null): string {
  const at = `at ${time} IST`;
  if (frequency === 'Daily') {
    return `Every day ${at}`;
  }
  if (frequency === 'Weekly') {
    return `Every ${dayOfWeek ?? 'Monday'} ${at}`;
  }
  const day = dayOfMonth ?? 1;
  return day >= LAST_DAY_OF_MONTH ? `On the last day of every month ${at}` : `On the ${ordinal(day)} of every month ${at}`;
}

const EMAIL = /^[^\s@,;<>"']+@[^\s@,;<>"']+\.[^\s@,;<>"']+$/;

export function isEmail(value: string): boolean {
  return value.length <= 254 && EMAIL.test(value);
}

/** Splits pasted text ("a@x.in, b@x.in; c@x.in") into lower-case addresses, keeping the invalid ones for feedback. */
export function parseEmails(text: string): { valid: string[]; invalid: string[] } {
  const valid: string[] = [];
  const invalid: string[] = [];
  for (const piece of text.split(/[\s,;]+/)) {
    const email = piece.trim().replace(/^<|>$/g, '').toLowerCase();
    if (!email) {
      continue;
    }
    if (!isEmail(email)) {
      invalid.push(piece.trim());
    } else if (!valid.includes(email)) {
      valid.push(email);
    }
  }
  return { valid, invalid };
}

// ------------------------------------------------------------------------------------------------ dashboards

/** Moves an item one place earlier or later (dashboard widget order). */
export function moveItem<T>(items: readonly T[], index: number, delta: -1 | 1): T[] {
  const target = index + delta;
  if (index < 0 || index >= items.length || target < 0 || target >= items.length) {
    return [...items];
  }
  const next = [...items];
  const [item] = next.splice(index, 1);
  next.splice(target, 0, item as T);
  return next;
}

/** A new widget id ("w7") unique among the existing ones. */
export function nextWidgetId(existing: readonly string[]): string {
  let highest = 0;
  for (const id of existing) {
    const match = /^w(\d+)$/.exec(id);
    if (match) {
      highest = Math.max(highest, Number(match[1]));
    }
  }
  let candidate = highest + 1;
  while (existing.includes(`w${candidate}`)) {
    candidate++;
  }
  return `w${candidate}`;
}

/** "revenue-by-department_20260901-20260915.xlsx" — the same name the server gives the file. */
export function exportFileName(reportId: string, from: string | null, to: string | null, today: string, extension: 'xlsx' | 'csv'): string {
  const compact = (iso: string) => iso.replaceAll('-', '');
  const stamp = from && to ? (from === to ? compact(from) : `${compact(from)}-${compact(to)}`) : compact(today);
  return `${reportId}_${stamp}.${extension}`;
}
