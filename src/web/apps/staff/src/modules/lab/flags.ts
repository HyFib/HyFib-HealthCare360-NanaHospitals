/**
 * Result interpretation on the workstation, mirroring the server rules (ResultInterpreter, ReferenceRangeSelector,
 * DeltaCheck) so a technician sees H/L/critical flags and delta warnings while typing. The server recomputes
 * everything on save and its answer is what gets stored and printed; these functions only preview it.
 */
import type { Gender } from '@hc360/core';
import type { LabResultType, ReferenceRangeDto, ResultFlag, ResultSheetLine } from './api';

export type FlagTone = 'normal' | 'low' | 'high' | 'abnormal' | 'critical';

export interface FlagDisplay {
  /** Printed code next to the value: H, L, HH, LL, A, C. Empty for normal results. */
  code: string;
  /** Words for screen readers and tooltips. */
  label: string;
  tone: FlagTone;
  critical: boolean;
}

/** How a flag is shown: code + words + tone. Colour is never the only signal (the code and words are always there). */
export function flagDisplay(flag: ResultFlag | null | undefined): FlagDisplay | null {
  switch (flag) {
    case 'Normal':
      return { code: '', label: 'Within the reference range', tone: 'normal', critical: false };
    case 'Low':
      return { code: 'L', label: 'Low', tone: 'low', critical: false };
    case 'High':
      return { code: 'H', label: 'High', tone: 'high', critical: false };
    case 'CriticalLow':
      return { code: 'LL', label: 'Critically low', tone: 'critical', critical: true };
    case 'CriticalHigh':
      return { code: 'HH', label: 'Critically high', tone: 'critical', critical: true };
    case 'Abnormal':
      return { code: 'A', label: 'Abnormal', tone: 'abnormal', critical: false };
    case 'Critical':
      return { code: 'C', label: 'Critical', tone: 'critical', critical: true };
    default:
      return null;
  }
}

export function isCriticalFlag(flag: ResultFlag | null | undefined): boolean {
  return flag === 'CriticalLow' || flag === 'CriticalHigh' || flag === 'Critical';
}

export interface ParsedNumber {
  comparator: '<' | '<=' | '>' | '>=' | null;
  value: number;
}

/**
 * Parses a numeric result like the server: "12.5", "-2.5", "1,20,000" (Indian digit grouping), "<0.01", "≥ 600".
 * Returns null when the text is not a number.
 */
export function parseNumericResult(raw: string | null | undefined): ParsedNumber | null {
  if (!raw) {
    return null;
  }
  let text = raw.replace(/\s/g, '').replace(/,/g, '').replace(/≤/g, '<=').replace(/≥/g, '>=');
  let comparator: ParsedNumber['comparator'] = null;
  for (const candidate of ['<=', '>=', '<', '>'] as const) {
    if (text.startsWith(candidate)) {
      comparator = candidate;
      text = text.slice(candidate.length);
      break;
    }
  }
  if (!/^[+-]?(\d+\.?\d*|\.\d+)$/.test(text)) {
    return null;
  }
  const value = Number(text);
  return Number.isFinite(value) ? { comparator, value } : null;
}

function isSexSpecific(range: ReferenceRangeDto): boolean {
  return range.gender === 'Male' || range.gender === 'Female';
}

function applies(range: ReferenceRangeDto, gender: Gender, ageDays: number | null): boolean {
  if (isSexSpecific(range) && range.gender !== gender) {
    return false;
  }
  if (ageDays === null) {
    return range.ageFromDays === 0 && range.ageToDays === null;
  }
  return ageDays >= range.ageFromDays && (range.ageToDays === null || ageDays < range.ageToDays);
}

/**
 * The reference interval for a patient: a sex-specific interval beats a sex-neutral one, then the narrowest age band
 * wins. With an unknown age only all-age intervals apply.
 */
export function selectRange(
  ranges: readonly ReferenceRangeDto[],
  gender: Gender,
  ageDays: number | null,
): ReferenceRangeDto | null {
  const width = (r: ReferenceRangeDto) => (r.ageToDays ?? Number.MAX_SAFE_INTEGER) - r.ageFromDays;
  const candidates = ranges.filter((r) => applies(r, gender, ageDays));
  candidates.sort(
    (a, b) =>
      Number(isSexSpecific(b)) - Number(isSexSpecific(a)) || width(a) - width(b) || b.ageFromDays - a.ageFromDays,
  );
  return candidates[0] ?? null;
}

interface NumericLimits {
  low: number | null;
  high: number | null;
  criticalLow: number | null;
  criticalHigh: number | null;
}

function hasLimits(range: NumericLimits): boolean {
  return range.low !== null || range.high !== null || range.criticalLow !== null || range.criticalHigh !== null;
}

/** Numeric flag; limits are exclusive (a value equal to a limit is inside the interval). */
export function classifyNumeric(range: NumericLimits, parsed: ParsedNumber): ResultFlag | null {
  const { value, comparator } = parsed;
  if (comparator === '<' || comparator === '<=') {
    if (range.criticalLow !== null) {
      return 'CriticalLow';
    }
    if (range.low !== null) {
      return 'Low';
    }
    const ceiling = range.high ?? range.criticalHigh;
    return ceiling !== null && value <= ceiling ? 'Normal' : null;
  }
  if (comparator === '>' || comparator === '>=') {
    if (range.criticalHigh !== null) {
      return 'CriticalHigh';
    }
    if (range.high !== null) {
      return 'High';
    }
    const floor = range.low ?? range.criticalLow;
    return floor !== null && value >= floor ? 'Normal' : null;
  }
  if (range.criticalLow !== null && value < range.criticalLow) {
    return 'CriticalLow';
  }
  if (range.criticalHigh !== null && value > range.criticalHigh) {
    return 'CriticalHigh';
  }
  if (range.low !== null && value < range.low) {
    return 'Low';
  }
  if (range.high !== null && value > range.high) {
    return 'High';
  }
  return hasLimits(range) ? 'Normal' : null;
}

function includesIgnoreCase(list: readonly string[] | null | undefined, value: string): boolean {
  const wanted = value.trim().toLowerCase();
  return (list ?? []).some((item) => item.trim().toLowerCase() === wanted);
}

/** Answer-type results: critical answers first, then normal answers; anything else is abnormal. */
export function classifyText(range: ReferenceRangeDto | null, value: string): ResultFlag | null {
  if (!range) {
    return null;
  }
  if (includesIgnoreCase(range.criticalValues, value)) {
    return 'Critical';
  }
  if (!range.normalValues || range.normalValues.length === 0) {
    return null;
  }
  return includesIgnoreCase(range.normalValues, value) ? 'Normal' : 'Abnormal';
}

/** Cultures: an isolated organism is always abnormal; listed organisms (e.g. in a blood culture) are critical. */
export function classifyCulture(
  range: ReferenceRangeDto | null,
  value: string,
  organism: string | null | undefined,
): ResultFlag | null {
  const hasOrganism = Boolean(organism?.trim());
  if (range && hasOrganism && includesIgnoreCase(range.criticalValues, organism!)) {
    return 'Critical';
  }
  const flag = classifyText(range, value);
  return hasOrganism && (flag === null || flag === 'Normal') ? 'Abnormal' : flag;
}

/**
 * Fallback when the test's intervals cannot be loaded: reads "13.0 - 17.0", "Up to 40" or "3.5 and above" from the
 * printed reference text. Critical limits are not in the text, so only H/L can be previewed this way.
 */
export function limitsFromReferenceText(text: string | null | undefined): NumericLimits | null {
  const value = text?.trim();
  if (!value) {
    return null;
  }
  const number = String.raw`[-+]?\d[\d,]*(?:\.\d+)?|[-+]?\.\d+`;
  const between = new RegExp(`^(${number})\\s*(?:-|–|to)\\s*(${number})$`, 'i').exec(value);
  if (between?.[1] && between[2]) {
    return { low: toNumber(between[1]), high: toNumber(between[2]), criticalLow: null, criticalHigh: null };
  }
  const upTo = new RegExp(`^(?:up to|upto|<=?|≤|less than)\\s*(${number})$`, 'i').exec(value);
  if (upTo?.[1]) {
    return { low: null, high: toNumber(upTo[1]), criticalLow: null, criticalHigh: null };
  }
  const above = new RegExp(
    `^(?:(${number})\\s*(?:and above|or more|\\+)|(?:>=?|≥|more than)\\s*(${number}))$`,
    'i',
  ).exec(value);
  const bound = above?.[1] ?? above?.[2];
  if (bound) {
    return { low: toNumber(bound), high: null, criticalLow: null, criticalHigh: null };
  }
  return null;
}

function toNumber(text: string): number {
  return Number(text.replace(/,/g, ''));
}

export interface DeltaOutcome {
  percent: number | null;
  exceeded: boolean;
}

/**
 * Delta check against the patient's previous verified result: |current − previous| ÷ |previous| × 100, one decimal.
 * A change away from zero has no percentage but counts as exceeded; a threshold of zero disables the check.
 */
export function deltaCheck(
  current: number | null,
  previous: number | null | undefined,
  thresholdPercent: number,
): DeltaOutcome {
  if (current === null || previous === null || previous === undefined) {
    return { percent: null, exceeded: false };
  }
  const enabled = thresholdPercent > 0;
  const difference = Math.abs(current - previous);
  if (previous === 0) {
    return { percent: difference === 0 ? 0 : null, exceeded: enabled && difference !== 0 };
  }
  const percent = Math.round((difference / Math.abs(previous)) * 1000) / 10;
  return { percent, exceeded: enabled && percent > thresholdPercent };
}

/** Facility default when a test has no delta threshold of its own (setting lab.deltaCheckPercent). */
export const DEFAULT_DELTA_PERCENT = 50;

export interface LineEdit {
  value: string;
  remarks?: string;
  organism?: string;
  colonyCount?: string;
}

export interface LineInterpretation {
  flag: ResultFlag | null;
  /** 'server' once saved and unchanged; 'preview' while typing. */
  source: 'server' | 'preview';
  invalid: string | null;
  deltaPercent: number | null;
  deltaExceeded: boolean;
  /** The typed value differs from the saved one. */
  dirty: boolean;
}

export interface InterpretationContext {
  gender: Gender;
  ageDays: number | null;
  /** Intervals of the analyte when the test master could be loaded. */
  ranges?: readonly ReferenceRangeDto[] | null;
  deltaThresholdPercent?: number | null;
  options?: readonly string[];
}

function sameValue(a: string | null | undefined, b: string | null | undefined): boolean {
  return (a ?? '').trim() === (b ?? '').trim();
}

/** Flag, validity and delta for one result line, from the saved line and what is being typed. */
export function interpretLine(
  line: Pick<
    ResultSheetLine,
    | 'resultType'
    | 'value'
    | 'flag'
    | 'referenceText'
    | 'previousValue'
    | 'deltaPercent'
    | 'deltaExceeded'
    | 'organism'
    | 'options'
  >,
  edit: LineEdit | undefined,
  context: InterpretationContext,
): LineInterpretation {
  const dirty =
    edit !== undefined &&
    (!sameValue(edit.value, line.value) || (edit.organism !== undefined && !sameValue(edit.organism, line.organism)));
  if (!dirty) {
    return {
      flag: line.flag,
      source: 'server',
      invalid: null,
      deltaPercent: line.deltaPercent,
      deltaExceeded: line.deltaExceeded,
      dirty: false,
    };
  }

  const value = edit.value.trim();
  const base: LineInterpretation = {
    flag: null,
    source: 'preview',
    invalid: null,
    deltaPercent: null,
    deltaExceeded: false,
    dirty: true,
  };
  if (!value) {
    return base;
  }

  const range = context.ranges ? selectRange(context.ranges, context.gender, context.ageDays) : null;
  const type: LabResultType = line.resultType;
  if (type === 'Numeric') {
    const parsed = parseNumericResult(value);
    if (!parsed) {
      return { ...base, invalid: 'Enter a number, for example 12.5, <0.01 or >600.' };
    }
    const limits: NumericLimits | null = range
      ? { low: range.low, high: range.high, criticalLow: range.criticalLow, criticalHigh: range.criticalHigh }
      : context.ranges
        ? null
        : limitsFromReferenceText(line.referenceText);
    const flag = limits ? classifyNumeric(limits, parsed) : null;
    const delta = parsed.comparator
      ? { percent: null, exceeded: false }
      : deltaCheck(parsed.value, line.previousValue, context.deltaThresholdPercent ?? DEFAULT_DELTA_PERCENT);
    return { ...base, flag, deltaPercent: delta.percent, deltaExceeded: delta.exceeded };
  }
  if (type === 'Options') {
    const options = context.options ?? line.options;
    if (options.length > 0 && !options.some((option) => option.toLowerCase() === value.toLowerCase())) {
      return { ...base, invalid: `Choose one of: ${options.join(', ')}.` };
    }
    return { ...base, flag: classifyText(range, value) };
  }
  if (type === 'Culture') {
    return { ...base, flag: classifyCulture(range, value, edit.organism ?? line.organism) };
  }
  return { ...base, flag: classifyText(range, value) };
}

/**
 * Why a pathologist should look at a result before verifying it (the same facts that stop auto-validation). An empty
 * list means the result is in range with nothing unusual, so it can be verified in bulk.
 */
export function reviewHints(
  line: Pick<ResultSheetLine, 'resultType' | 'status' | 'autoValidated' | 'hasCriticalCall' | 'remarks'>,
  interpretation: Pick<LineInterpretation, 'flag' | 'deltaExceeded' | 'invalid'>,
): string[] {
  const hints: string[] = [];
  if (interpretation.invalid) {
    hints.push(interpretation.invalid);
  }
  if (isCriticalFlag(interpretation.flag)) {
    hints.push(
      line.hasCriticalCall ? 'Critical value (call documented)' : 'Critical value: document the call to the doctor',
    );
  } else if (interpretation.flag && interpretation.flag !== 'Normal') {
    hints.push(`Flagged ${flagDisplay(interpretation.flag)?.label.toLowerCase() ?? interpretation.flag}`);
  } else if (interpretation.flag === null && line.resultType === 'Numeric') {
    hints.push('No reference range applies to this patient');
  }
  if (interpretation.deltaExceeded) {
    hints.push('Large change from the previous result');
  }
  if (line.resultType === 'Culture') {
    hints.push('Culture: review organism and sensitivity');
  }
  if (line.remarks) {
    hints.push('Has remarks');
  }
  return hints;
}

/** True when an entered result may be verified together with others without an individual look. */
export function isReadyForBulkVerify(
  line: Pick<ResultSheetLine, 'status' | 'resultType' | 'autoValidated' | 'hasCriticalCall' | 'remarks'>,
  interpretation: Pick<LineInterpretation, 'flag' | 'deltaExceeded' | 'invalid' | 'dirty'>,
): boolean {
  return (
    line.status === 'Entered' &&
    !interpretation.dirty &&
    reviewHints(line, interpretation).length === 0 &&
    interpretation.flag === 'Normal'
  );
}

/** "+12.5 %" / "−40 %" with the direction of the change, for the previous-result column. */
export function formatDelta(
  current: number | null,
  previous: number | null | undefined,
  percent: number | null,
): string | null {
  if (current === null || previous === null || previous === undefined) {
    return null;
  }
  const direction = current > previous ? '▲' : current < previous ? '▼' : '';
  if (percent === null) {
    return current === previous ? 'no change' : `${direction} from 0`;
  }
  return `${direction} ${percent.toLocaleString('en-IN', { maximumFractionDigits: 1 })}%`.trim();
}
