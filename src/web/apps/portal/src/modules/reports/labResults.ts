import type { Tone } from '@/components/ListRow';
import type { MessageKey } from '@/i18n';
import type { CardiologyStudyType, LabReportSection, LabSection, Modality, ResultFlag, SusceptibilityInterpretation } from '@/lib/types';

/**
 * Lab results in plain language (pure functions, unit-tested): what each flag means, how serious it is, and where a
 * value sits against its normal range. Colour is never the only signal: every flag also has words and an icon.
 */

export interface FlagInfo {
  key: MessageKey;
  tone: Tone;
  /** 0 normal, 1 outside the range, 2 needs prompt attention. */
  severity: 0 | 1 | 2;
}

export const FLAG_INFO: Record<ResultFlag, FlagInfo> = {
  Normal: { key: 'lab.flag.Normal', tone: 'success', severity: 0 },
  Low: { key: 'lab.flag.Low', tone: 'warning', severity: 1 },
  High: { key: 'lab.flag.High', tone: 'warning', severity: 1 },
  Abnormal: { key: 'lab.flag.Abnormal', tone: 'warning', severity: 1 },
  CriticalLow: { key: 'lab.flag.CriticalLow', tone: 'danger', severity: 2 },
  CriticalHigh: { key: 'lab.flag.CriticalHigh', tone: 'danger', severity: 2 },
  Critical: { key: 'lab.flag.Critical', tone: 'danger', severity: 2 },
};

export const SECTION_KEY: Record<LabSection, MessageKey> = {
  Hematology: 'lab.section.Hematology',
  Biochemistry: 'lab.section.Biochemistry',
  Microbiology: 'lab.section.Microbiology',
  Serology: 'lab.section.Serology',
  ClinicalPathology: 'lab.section.ClinicalPathology',
  Histopathology: 'lab.section.Histopathology',
  Immunology: 'lab.section.Immunology',
};

export const SUSCEPTIBILITY_KEY: Record<SusceptibilityInterpretation, MessageKey> = {
  Sensitive: 'lab.sensitivity.Sensitive',
  Intermediate: 'lab.sensitivity.Intermediate',
  Resistant: 'lab.sensitivity.Resistant',
};

export const MODALITY_KEY: Record<Modality, MessageKey> = {
  XR: 'rad.modality.XR',
  DX: 'rad.modality.DX',
  CT: 'rad.modality.CT',
  MR: 'rad.modality.MR',
  US: 'rad.modality.US',
  MG: 'rad.modality.MG',
  RF: 'rad.modality.RF',
};

export const CARDIOLOGY_TYPE_KEY: Record<CardiologyStudyType, MessageKey> = {
  Ecg: 'card.type.Ecg',
  Echo: 'card.type.Echo',
  Tmt: 'card.type.Tmt',
  Holter: 'card.type.Holter',
  Abpm: 'card.type.Abpm',
  Other: 'card.type.Other',
};

export function flagInfo(flag: ResultFlag | null | undefined): FlagInfo | null {
  return flag ? FLAG_INFO[flag] : null;
}

export interface ResultSummary {
  /** Results that have a value. */
  total: number;
  /** Of those, how many are outside the normal range. */
  outOfRange: number;
  /** True when any result needs prompt attention. */
  critical: boolean;
}

export function summarizeResults(sections: readonly LabReportSection[]): ResultSummary {
  let total = 0;
  let outOfRange = 0;
  let critical = false;
  for (const section of sections) {
    for (const test of section.tests) {
      for (const line of test.results) {
        if (line.value === null || line.value.trim() === '') {
          continue;
        }
        total += 1;
        const info = flagInfo(line.flag);
        if (info && info.severity > 0) {
          outOfRange += 1;
          critical ||= info.severity === 2;
        }
      }
    }
  }
  return { total, outOfRange, critical };
}

/** A number written on a report ("13.2", "1,50,000", "-0.5"); null for text such as "Negative" or "Reactive". */
export function parseResultNumber(value: string | null | undefined): number | null {
  const text = value?.trim().replace(/,/g, '');
  if (!text || !/^[-+]?\d+(\.\d+)?$/.test(text)) {
    return null;
  }
  return Number(text);
}

export interface NumericRange {
  low: number | null;
  high: number | null;
}

/**
 * Reads a numeric reference range from the report's text: "12.0 - 15.5", "12–15.5 g/dL", "12 to 15.5", "< 200",
 * "Up to 200", "> 40", "≥ 40". Anything else (e.g. "Negative") gives null and no range bar is drawn.
 */
export function parseRange(text: string | null | undefined): NumericRange | null {
  const value = text?.replace(/,/g, '').trim();
  if (!value) {
    return null;
  }
  const number = '(-?\\d+(?:\\.\\d+)?)';
  const between = new RegExp(`^${number}\\s*(?:-|–|—|to)\\s*${number}`, 'i').exec(value);
  if (between) {
    const low = Number(between[1]);
    const high = Number(between[2]);
    return low < high ? { low, high } : null;
  }
  const below = new RegExp(`^(?:<|<=|≤|up to|upto|less than)\\s*${number}`, 'i').exec(value);
  if (below) {
    return { low: null, high: Number(below[1]) };
  }
  const above = new RegExp(`^(?:>|>=|≥|more than|greater than)\\s*${number}`, 'i').exec(value);
  if (above) {
    return { low: Number(above[1]), high: null };
  }
  return null;
}

/** Where the marker sits on a bar whose middle half (25–75 %) is the normal range; 0–1, clamped. */
export function rangePosition(value: number, range: NumericRange): number {
  const clamp = (x: number) => Math.min(1, Math.max(0, x));
  const { low, high } = range;
  if (low !== null && high !== null) {
    const span = high - low;
    if (value < low) {
      return clamp(0.25 - (0.25 * (low - value)) / span);
    }
    if (value > high) {
      return clamp(0.75 + (0.25 * (value - high)) / span);
    }
    return 0.25 + (0.5 * (value - low)) / span;
  }
  if (high !== null) {
    // "< 200": normal from the left edge up to 75 %.
    return value <= high ? clamp((0.75 * value) / (high || 1)) : clamp(0.75 + (0.25 * (value - high)) / (Math.abs(high) || 1));
  }
  if (low !== null) {
    // "> 40": normal from 25 % to the right edge.
    return value >= low ? clamp(0.25 + (0.75 * (value - low)) / (Math.abs(low) || 1)) : clamp((0.25 * value) / (low || 1));
  }
  return 0.5;
}
