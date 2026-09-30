/**
 * Pure feedback rules mirrored from the Engagement server (NpsCalculator, LowScoreRule, Complaint) plus the NPS gauge
 * geometry. The server stays the authority and checks everything again.
 */
import type { RatingsInput } from './api';
import type { ComplaintPriority, ComplaintStatus, FeedbackAspect, Sentiment } from './labels';

type Tone = 'danger' | 'warning' | 'success' | 'brand' | 'informative';

// ------------------------------------------------------------------ NPS

export type NpsGroup = 'Promoter' | 'Passive' | 'Detractor';

/** 9–10 promoters, 7–8 passives, 0–6 detractors. */
export function npsGroup(score: number): NpsGroup {
  if (score >= 9) {
    return 'Promoter';
  }
  return score >= 7 ? 'Passive' : 'Detractor';
}

/** NPS = % promoters − % detractors, to one decimal rounded half away from zero like the server (null without answers). */
export function npsScore(answers: number[]): number | null {
  const valid = answers.filter((a) => Number.isInteger(a) && a >= 0 && a <= 10);
  if (valid.length === 0) {
    return null;
  }
  const promoters = valid.filter((a) => a >= 9).length;
  const detractors = valid.filter((a) => a <= 6).length;
  const tenths = ((promoters - detractors) * 1000) / valid.length;
  return (Math.sign(tenths) * Math.round(Math.abs(tenths))) / 10 || 0;
}

/** Text for an NPS value: "+42.5", "−10", "0". */
export function npsText(score: number | null | undefined): string {
  if (score === null || score === undefined) {
    return '—';
  }
  const text = Math.abs(score).toLocaleString('en-IN', { maximumFractionDigits: 1 });
  return score > 0 ? `+${text}` : score < 0 ? `−${text}` : '0';
}

/** Plain-words band used across NHS and hospital benchmarks. */
export function npsBand(score: number | null): { label: string; tone: Tone } {
  if (score === null) {
    return { label: 'Not enough answers', tone: 'informative' };
  }
  if (score >= 50) {
    return { label: 'Excellent', tone: 'success' };
  }
  if (score >= 30) {
    return { label: 'Good', tone: 'brand' };
  }
  if (score >= 0) {
    return { label: 'Needs improvement', tone: 'warning' };
  }
  return { label: 'Poor', tone: 'danger' };
}

/** Gauge needle angle in degrees: −100 → 180° (left), 0 → 90° (up), +100 → 0° (right). */
export function npsAngle(score: number): number {
  const clamped = Math.max(-100, Math.min(100, score));
  return 90 - (clamped * 90) / 100;
}

/** Point on the gauge arc for an angle (degrees), centre (cx, cy) and radius r; y grows downwards (SVG). */
export function arcPoint(cx: number, cy: number, r: number, angleDegrees: number): { x: number; y: number } {
  const radians = (angleDegrees * Math.PI) / 180;
  return {
    x: Math.round((cx + r * Math.cos(radians)) * 100) / 100,
    y: Math.round((cy - r * Math.sin(radians)) * 100) / 100,
  };
}

// ------------------------------------------------------------------ ratings

/** What a patient (or staff on their behalf) answered on a form. */
export interface FeedbackAnswers {
  ratings: Partial<Record<FeedbackAspect, number | null>>;
  nps: number | null;
  comment: string;
}

export const EMPTY_ANSWERS: FeedbackAnswers = { ratings: {}, nps: null, comment: '' };

/** JSON key of an aspect in the server's RatingsInput ("FrontDesk" → "frontDesk"). */
export function ratingKey(aspect: FeedbackAspect): keyof RatingsInput {
  return (aspect.charAt(0).toLowerCase() + aspect.slice(1)) as keyof RatingsInput;
}

/** Ratings for the aspects on the form only (unanswered ones left out); null when nothing was rated. */
export function toRatingsInput(
  aspects: readonly FeedbackAspect[],
  values: Partial<Record<FeedbackAspect, number | null>>,
): RatingsInput | null {
  const ratings: RatingsInput = {};
  let any = false;
  for (const aspect of aspects) {
    const value = values[aspect];
    if (value !== null && value !== undefined) {
      ratings[ratingKey(aspect)] = value;
      any = true;
    }
  }
  return any ? ratings : null;
}

/** A rating of 2 or less on any aspect, or an NPS of 6 or less, is a low score and raises a complaint (FB-03). */
export function isLowScore(ratings: (number | null | undefined)[], nps: number | null | undefined): boolean {
  return (
    ratings.some((r) => r !== null && r !== undefined && r <= 2) || (nps !== null && nps !== undefined && nps <= 6)
  );
}

/** Priority the server gives the complaint a low score raises: High for a 1-star rating or NPS 3 or less. */
export function lowScorePriority(
  ratings: (number | null | undefined)[],
  nps: number | null | undefined,
): ComplaintPriority {
  return ratings.some((r) => r !== null && r !== undefined && r <= 1) || (nps !== null && nps !== undefined && nps <= 3)
    ? 'High'
    : 'Normal';
}

export function ratingText(value: number | null | undefined): string {
  return value === null || value === undefined
    ? '—'
    : `${value.toLocaleString('en-IN', { maximumFractionDigits: 1 })} / 5`;
}

export function sentimentTone(sentiment: Sentiment): Tone {
  return sentiment === 'Positive' ? 'success' : sentiment === 'Negative' ? 'danger' : 'informative';
}

// ------------------------------------------------------------------ complaints

/** Resolution deadline from when the complaint is raised: 24 h for High, 72 h for Normal. */
export const SLA_HOURS: Record<ComplaintPriority, number> = { High: 24, Normal: 72 };

/** Allowed moves (Complaint.MoveTo). */
export function complaintMoves(status: ComplaintStatus): ComplaintStatus[] {
  switch (status) {
    case 'Open':
      return ['InProgress', 'Resolved'];
    case 'InProgress':
      return ['Resolved'];
    case 'Resolved':
      return ['Closed', 'InProgress'];
    default:
      return [];
  }
}

/** Resolving needs the resolution; reopening needs the reason. */
export function complaintNoteRequired(from: ComplaintStatus, to: ComplaintStatus): boolean {
  return to === 'Resolved' || (from === 'Resolved' && to === 'InProgress');
}

export function complaintMoveLabel(from: ComplaintStatus, to: ComplaintStatus): string {
  if (from === 'Resolved' && to === 'InProgress') {
    return 'Reopen';
  }
  switch (to) {
    case 'InProgress':
      return 'Start handling';
    case 'Resolved':
      return 'Mark resolved';
    case 'Closed':
      return 'Close';
    default:
      return to;
  }
}

/** "4 h left", "Due in 2 days", "Overdue by 5 h" against the resolution deadline. */
export function deadlineText(dueAtIso: string, nowMs: number): { text: string; overdue: boolean } {
  const minutes = Math.round((Date.parse(dueAtIso) - nowMs) / 60_000);
  const abs = Math.abs(minutes);
  const span =
    abs < 60 ? `${abs} min` : abs < 48 * 60 ? `${Math.round(abs / 60)} h` : `${Math.round(abs / (24 * 60))} days`;
  return minutes < 0 ? { text: `Overdue by ${span}`, overdue: true } : { text: `${span} left`, overdue: false };
}

// ------------------------------------------------------------------ periods

export const PERIOD_PRESETS = ['thisMonth', 'lastMonth', 'last3Months', 'last12Months', 'custom'] as const;
export type PeriodPreset = (typeof PERIOD_PRESETS)[number];
export const periodPresetLabel: Record<PeriodPreset, string> = {
  thisMonth: 'This month',
  lastMonth: 'Last month',
  last3Months: 'Last 3 months',
  last12Months: 'Last 12 months',
  custom: 'Choose dates',
};

/** Inclusive date range for a preset, from today's hospital date ("yyyy-MM-dd"). Month presets start on the 1st. */
export function presetRange(preset: Exclude<PeriodPreset, 'custom'>, today: string): { from: string; to: string } {
  const year = Number(today.slice(0, 4));
  const month = Number(today.slice(5, 7));
  const first = (offset: number) => {
    const date = new Date(Date.UTC(year, month - 1 + offset, 1));
    return { y: date.getUTCFullYear(), m: date.getUTCMonth() + 1 };
  };
  const start = (offset: number) => {
    const { y, m } = first(offset);
    return `${y}-${String(m).padStart(2, '0')}-01`;
  };
  switch (preset) {
    case 'thisMonth':
      return { from: start(0), to: today };
    case 'lastMonth': {
      const { y, m } = first(-1);
      const last = new Date(Date.UTC(y, m, 0)).getUTCDate();
      return { from: start(-1), to: `${y}-${String(m).padStart(2, '0')}-${String(last).padStart(2, '0')}` };
    }
    case 'last3Months':
      return { from: start(-2), to: today };
    default:
      return { from: start(-11), to: today };
  }
}

/** The period a screen shows: a preset, or the chosen dates (swapped when entered backwards). Defaults to the last 12 months. */
export function resolvePeriod(
  preset: PeriodPreset | undefined,
  from: string | undefined,
  to: string | undefined,
  today: string,
): { from: string; to: string } {
  if (preset !== 'custom') {
    return presetRange(preset ?? 'last12Months', today);
  }
  const start = from ?? presetRange('last12Months', today).from;
  const end = to ?? today;
  return start <= end ? { from: start, to: end } : { from: end, to: start };
}

/** Turnaround in words: "5 h", "2.5 days". */
export function turnaroundText(hours: number | null | undefined): string {
  if (hours === null || hours === undefined) {
    return '—';
  }
  if (hours < 48) {
    return `${Math.round(hours * 10) / 10} h`;
  }
  return `${Math.round((hours / 24) * 10) / 10} days`;
}
