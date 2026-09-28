/**
 * Cardiology measurement helpers for the report editor (pure; unit-tested). They calculate values the cardiologist
 * would otherwise work out by hand and give short reference hints; the cardiologist's interpretation is what the report
 * says.
 */
import type { Gender } from '@hc360/core';

function valid(value: number | null | undefined): value is number {
  return value !== null && value !== undefined && Number.isFinite(value);
}

/** RR interval in seconds for a heart rate. */
function rrSeconds(heartRate: number): number {
  return 60 / heartRate;
}

/** Bazett's corrected QT (ms): QT / √RR. */
export function bazettQtc(qtMs: number | null | undefined, heartRate: number | null | undefined): number | null {
  if (!valid(qtMs) || !valid(heartRate) || qtMs <= 0 || heartRate <= 0) {
    return null;
  }
  return Math.round(qtMs / Math.sqrt(rrSeconds(heartRate)));
}

/** Fridericia's corrected QT (ms): QT / ∛RR (better than Bazett at fast or slow heart rates). */
export function fridericiaQtc(qtMs: number | null | undefined, heartRate: number | null | undefined): number | null {
  if (!valid(qtMs) || !valid(heartRate) || qtMs <= 0 || heartRate <= 0) {
    return null;
  }
  return Math.round(qtMs / Math.cbrt(rrSeconds(heartRate)));
}

/** Age-predicted maximum heart rate (220 − age). */
export function predictedMaxHeartRate(ageYears: number | null | undefined): number | null {
  return valid(ageYears) && ageYears > 0 && ageYears < 120 ? 220 - Math.round(ageYears) : null;
}

/** Share of the age-predicted maximum heart rate reached on the treadmill (85 % is the usual target). */
export function targetHeartRatePercent(maxHeartRate: number | null | undefined, ageYears: number | null | undefined): number | null {
  const predicted = predictedMaxHeartRate(ageYears);
  if (!valid(maxHeartRate) || predicted === null || maxHeartRate <= 0) {
    return null;
  }
  return Math.round((maxHeartRate / predicted) * 100);
}

export interface Dipping {
  percent: number;
  pattern: 'Reverse dipper' | 'Non-dipper' | 'Dipper' | 'Extreme dipper';
}

/** Nocturnal dip of systolic pressure: (day − night) ÷ day × 100 and its usual name. */
export function dippingPattern(daySystolic: number | null | undefined, nightSystolic: number | null | undefined): Dipping | null {
  if (!valid(daySystolic) || !valid(nightSystolic) || daySystolic <= 0) {
    return null;
  }
  const percent = Math.round(((daySystolic - nightSystolic) / daySystolic) * 1000) / 10;
  const pattern = percent < 0 ? 'Reverse dipper' : percent < 10 ? 'Non-dipper' : percent <= 20 ? 'Dipper' : 'Extreme dipper';
  return { percent, pattern };
}

/** "12:30" or "12" (minutes) → seconds; null when not a duration. */
export function parseDuration(text: string | null | undefined): number | null {
  const value = text?.trim();
  if (!value) {
    return null;
  }
  const clock = /^(\d{1,3}):([0-5]\d)$/.exec(value);
  if (clock) {
    return Number(clock[1]) * 60 + Number(clock[2]);
  }
  return /^\d{1,3}$/.test(value) ? Number(value) * 60 : null;
}

/** Seconds → "12:30". */
export function formatDuration(seconds: number | null | undefined): string {
  if (!valid(seconds) || seconds < 0) {
    return '';
  }
  const whole = Math.round(seconds);
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

/** LVEF category (universal definition of heart failure): ≤ 40 reduced, 41–49 mildly reduced, ≥ 50 preserved. */
export function lvefCategory(lvef: number | null | undefined): string | null {
  if (!valid(lvef)) {
    return null;
  }
  if (lvef <= 40) {
    return 'Reduced (40% or less)';
  }
  return lvef < 50 ? 'Mildly reduced (41–49%)' : 'Preserved (50% or more)';
}

/** QTc reference hint: above 450 ms (men) or 460 ms (women) is prolonged; above 500 ms is markedly prolonged. */
export function qtcHint(qtc: number | null | undefined, gender: Gender): string | null {
  if (!valid(qtc)) {
    return null;
  }
  if (qtc > 500) {
    return 'Markedly prolonged (above 500 ms)';
  }
  const limit = gender === 'Female' ? 460 : 450;
  return qtc > limit ? `Prolonged (above ${limit} ms)` : 'Within normal limits';
}

/** Resting heart-rate hint: below 60 bradycardia, above 100 tachycardia. */
export function heartRateHint(heartRate: number | null | undefined): string | null {
  if (!valid(heartRate)) {
    return null;
  }
  return heartRate < 60 ? 'Bradycardia (below 60/min)' : heartRate > 100 ? 'Tachycardia (above 100/min)' : 'Normal rate';
}
