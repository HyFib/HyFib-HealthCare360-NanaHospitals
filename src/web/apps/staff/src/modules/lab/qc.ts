/**
 * Quality-control helpers for the Levey-Jennings chart and the QC board (pure; unit-tested). The server judges runs
 * with the Westgard rules; these functions only draw what it decided.
 */
import type { QcOutcome, QcStatusRow } from './api';

export interface SdLine {
  /** "-3SD" … "Mean" … "+3SD", as the server labels its limit lines. */
  label: string;
  /** Distance from the mean in SDs (-3 … 3). */
  sds: number;
  value: number;
}

/** Mean and ±1/2/3 SD limit lines for a control lot. */
export function sdLines(mean: number, sd: number): SdLine[] {
  return [-3, -2, -1, 0, 1, 2, 3].map((sds) => ({
    label: sds === 0 ? 'Mean' : `${sds > 0 ? '+' : ''}${sds}SD`,
    sds,
    value: round(mean + sds * sd, 6),
  }));
}

/** Z-score of a control value against the lot target (0 when the SD is not usable). */
export function zScore(value: number, mean: number, sd: number): number {
  return sd > 0 ? (value - mean) / sd : 0;
}

export type SdZone = 'within1' | 'within2' | 'within3' | 'beyond3';

/** Band a point falls in. Limits are strict like the Westgard evaluation: exactly 2 SD is still within 2 SD. */
export function sdZone(z: number): SdZone {
  const distance = Math.abs(z);
  if (distance <= 1) {
    return 'within1';
  }
  if (distance <= 2) {
    return 'within2';
  }
  return distance <= 3 ? 'within3' : 'beyond3';
}

/**
 * Y-axis range for the chart: at least mean ± 4 SD so the 3 SD lines are never on the edge, widened to keep outliers
 * visible with half an SD of margin.
 */
export function chartBounds(mean: number, sd: number, values: readonly number[]): { min: number; max: number } {
  const spread = sd > 0 ? sd : Math.max(Math.abs(mean) * 0.1, 1);
  let min = mean - 4 * spread;
  let max = mean + 4 * spread;
  for (const value of values) {
    min = Math.min(min, value - spread / 2);
    max = Math.max(max, value + spread / 2);
  }
  return { min: round(min, 6), max: round(max, 6) };
}

/** Chart colours (canvas cannot read CSS variables, so fixed values that work on light and dark backgrounds). */
export const QC_COLORS = {
  mean: '#0A7CA7',
  sd1: '#8A8886',
  sd2: '#C19C00',
  sd3: '#D13438',
  accepted: '#0A7CA7',
  warning: '#C19C00',
  rejected: '#D13438',
} as const;

export function outcomeColor(outcome: QcOutcome): string {
  return outcome === 'Rejected'
    ? QC_COLORS.rejected
    : outcome === 'AcceptedWithWarning'
      ? QC_COLORS.warning
      : QC_COLORS.accepted;
}

/** Line style for a limit line: dotted at 1 SD, dashed at 2 SD, solid at 3 SD and the mean. */
export function sdLineStyle(sds: number): { color: string; type: 'solid' | 'dashed' | 'dotted'; width: number } {
  const distance = Math.abs(sds);
  if (distance === 0) {
    return { color: QC_COLORS.mean, type: 'solid', width: 1.5 };
  }
  if (distance === 1) {
    return { color: QC_COLORS.sd1, type: 'dotted', width: 1 };
  }
  if (distance === 2) {
    return { color: QC_COLORS.sd2, type: 'dashed', width: 1 };
  }
  return { color: QC_COLORS.sd3, type: 'solid', width: 1 };
}

export interface QcBoardSummary {
  lots: number;
  accepted: number;
  warnings: number;
  rejected: number;
  notRun: number;
  lockedTests: string[];
}

/** Today's QC board in numbers, with the assays whose verification is locked. */
export function summarizeQc(rows: readonly QcStatusRow[]): QcBoardSummary {
  const locked = new Map<string, string>();
  let accepted = 0;
  let warnings = 0;
  let rejected = 0;
  let notRun = 0;
  for (const row of rows) {
    switch (row.latestOutcome) {
      case 'Accepted':
        accepted++;
        break;
      case 'AcceptedWithWarning':
        warnings++;
        break;
      case 'Rejected':
        rejected++;
        break;
      default:
        notRun++;
    }
    if (row.locked) {
      locked.set(row.labTestId, row.testName);
    }
  }
  return { lots: rows.length, accepted, warnings, rejected, notRun, lockedTests: [...locked.values()] };
}

/** "+2.35 SD" / "−1.10 SD" for tooltips and tables. */
export function formatZ(z: number): string {
  const sign = z > 0 ? '+' : z < 0 ? '−' : '';
  return `${sign}${Math.abs(z).toFixed(2)} SD`;
}

function round(value: number, digits: number): number {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}
