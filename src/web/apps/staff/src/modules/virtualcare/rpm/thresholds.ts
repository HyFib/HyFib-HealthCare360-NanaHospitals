import type { ReadingSeverity } from './api';

/**
 * Reading limits (RPM-03), mirroring the server's `ThresholdRange` and `ThresholdEvaluator`: inside [min, max] is
 * normal, outside it a warning, beyond [criticalMin, criticalMax] critical. Any bound may be missing (SpO2 has no
 * upper limit). Pure functions for the trend charts and the limits editor.
 */
export interface Limits {
  min: number | null;
  max: number | null;
  criticalMin: number | null;
  criticalMax: number | null;
}

export function evaluateSeverity(value: number, limits: Limits | null | undefined): ReadingSeverity {
  if (!limits) {
    return 'Normal';
  }
  if (
    (limits.criticalMin !== null && value < limits.criticalMin) ||
    (limits.criticalMax !== null && value > limits.criticalMax)
  ) {
    return 'Critical';
  }
  if ((limits.min !== null && value < limits.min) || (limits.max !== null && value > limits.max)) {
    return 'Warning';
  }
  return 'Normal';
}

export interface Band {
  from: number;
  to: number;
  severity: ReadingSeverity;
}

/** Horizontal bands (bottom to top) that colour a chart's background by severity, clipped to the axis. */
export function thresholdBands(limits: Limits, axis: { min: number; max: number }): Band[] {
  const { min, max, criticalMin, criticalMax } = limits;
  const bands: Band[] = [];
  if (criticalMin !== null) {
    bands.push({ from: axis.min, to: criticalMin, severity: 'Critical' });
  }
  if (min !== null) {
    bands.push({ from: criticalMin ?? axis.min, to: min, severity: 'Warning' });
  }
  bands.push({ from: min ?? criticalMin ?? axis.min, to: max ?? criticalMax ?? axis.max, severity: 'Normal' });
  if (max !== null) {
    bands.push({ from: max, to: criticalMax ?? axis.max, severity: 'Warning' });
  }
  if (criticalMax !== null) {
    bands.push({ from: criticalMax, to: axis.max, severity: 'Critical' });
  }
  return bands
    .map((band) => ({ ...band, from: Math.max(axis.min, band.from), to: Math.min(axis.max, band.to) }))
    .filter((band) => band.to > band.from);
}

/**
 * A y-axis range that shows every reading and every limit with some air, rounded to readable steps and kept within
 * what is physically possible for the measurement.
 */
export function axisRange(
  values: readonly number[],
  limits: Limits | null,
  plausible: { min: number; max: number },
): { min: number; max: number } {
  const candidates = [
    ...values,
    ...(limits ? [limits.min, limits.max, limits.criticalMin, limits.criticalMax] : []),
  ].filter((v): v is number => typeof v === 'number' && Number.isFinite(v));
  if (candidates.length === 0) {
    return { min: plausible.min, max: plausible.max };
  }
  const low = Math.min(...candidates);
  const high = Math.max(...candidates);
  const span = high - low || Math.max(1, Math.abs(high) * 0.1);
  const pad = span * 0.15;
  const step = span > 100 ? 10 : span > 20 ? 5 : span > 5 ? 1 : 0.5;
  const min = Math.max(plausible.min, Math.floor((low - pad) / step) * step);
  const max = Math.min(plausible.max, Math.ceil((high + pad) / step) * step);
  return { min, max: max > min ? max : min + step };
}

export interface LimitProblem {
  field: keyof Limits;
  message: string;
}

/** The server's `ThresholdRange.Validate`, so the editor shows problems before saving. */
export function validateLimits(limits: Limits): LimitProblem | null {
  const { min, max, criticalMin, criticalMax } = limits;
  if (min === null && max === null && criticalMin === null && criticalMax === null) {
    return { field: 'min', message: 'Set at least one limit.' };
  }
  if (min !== null && max !== null && min >= max) {
    return { field: 'max', message: 'The upper limit must be above the lower limit.' };
  }
  const ordered: [keyof Limits, number | null][] = [
    ['criticalMin', criticalMin],
    ['min', min],
    ['max', max],
    ['criticalMax', criticalMax],
  ];
  const present = ordered.filter((entry): entry is [keyof Limits, number] => entry[1] !== null);
  for (let i = 1; i < present.length; i++) {
    if (present[i]![1] < present[i - 1]![1]) {
      const culprit = present[i - 1]![0] === 'criticalMin' ? 'criticalMin' : 'criticalMax';
      return { field: culprit, message: 'Critical limits must lie outside the normal range.' };
    }
  }
  return null;
}

/** "90–140 mmHg (critical below 80 or above 180)". */
export function describeLimits(limits: Limits | null, unit: string): string {
  if (!limits) {
    return 'No limits set';
  }
  const u = unit === '%' ? '%' : ` ${unit}`;
  const normal =
    limits.min !== null && limits.max !== null
      ? `${limits.min}–${limits.max}${u}`
      : limits.min !== null
        ? `${limits.min}${u} or more`
        : limits.max !== null
          ? `up to ${limits.max}${u}`
          : 'any value';
  const critical = [
    limits.criticalMin !== null ? `below ${limits.criticalMin}` : null,
    limits.criticalMax !== null ? `above ${limits.criticalMax}` : null,
  ].filter(Boolean);
  return critical.length > 0 ? `${normal} (critical ${critical.join(' or ')})` : normal;
}
