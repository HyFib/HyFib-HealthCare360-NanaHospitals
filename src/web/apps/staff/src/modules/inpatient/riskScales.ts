/**
 * Nursing risk scales, mirroring the server (`MorseFallScale`, `BradenScale`, `PainScale`) so the form shows the score
 * and the action to take while the nurse is still answering. The server recalculates and stores the official score.
 */

export type RiskLevel = 'None' | 'Low' | 'Moderate' | 'High' | 'VeryHigh';

export interface RiskScore {
  score: number;
  risk: RiskLevel;
  interpretation: string;
}

export const RISK_LABEL: Record<RiskLevel, string> = {
  None: 'No risk',
  Low: 'Low',
  Moderate: 'Moderate',
  High: 'High',
  VeryHigh: 'Very high',
};

export function riskTone(risk: string): 'success' | 'informative' | 'warning' | 'danger' {
  switch (risk) {
    case 'None':
      return 'success';
    case 'Low':
      return 'informative';
    case 'Moderate':
      return 'warning';
    default:
      return 'danger';
  }
}

// ------------------------------------------------------------------ Morse Fall Scale

export type AmbulatoryAid = 'None' | 'CrutchesCaneWalker' | 'Furniture';
export type GaitType = 'Normal' | 'Weak' | 'Impaired';

export interface MorseInput {
  historyOfFalling: boolean;
  secondaryDiagnosis: boolean;
  ambulatoryAid: AmbulatoryAid;
  intravenousAccess: boolean;
  gait: GaitType;
  overestimatesAbility: boolean;
}

export const AMBULATORY_AID_OPTIONS: { value: AmbulatoryAid; label: string; points: number }[] = [
  { value: 'None', label: 'None, bed rest or nurse assisted', points: 0 },
  { value: 'CrutchesCaneWalker', label: 'Crutches, cane or walker', points: 15 },
  { value: 'Furniture', label: 'Holds on to furniture to walk', points: 30 },
];

export const GAIT_OPTIONS: { value: GaitType; label: string; points: number }[] = [
  { value: 'Normal', label: 'Normal, bed rest or wheelchair', points: 0 },
  { value: 'Weak', label: 'Weak', points: 10 },
  { value: 'Impaired', label: 'Impaired', points: 20 },
];

export const MORSE_HIGH_RISK_FROM = 45;
export const MORSE_MODERATE_RISK_FROM = 25;

export function morseScore(input: MorseInput): RiskScore {
  const score =
    (input.historyOfFalling ? 25 : 0) +
    (input.secondaryDiagnosis ? 15 : 0) +
    (AMBULATORY_AID_OPTIONS.find((o) => o.value === input.ambulatoryAid)?.points ?? 0) +
    (input.intravenousAccess ? 20 : 0) +
    (GAIT_OPTIONS.find((o) => o.value === input.gait)?.points ?? 0) +
    (input.overestimatesAbility ? 15 : 0);

  if (score >= MORSE_HIGH_RISK_FROM) {
    return {
      score,
      risk: 'High',
      interpretation: 'High fall risk: apply high-risk fall precautions and reassess every shift.',
    };
  }
  if (score >= MORSE_MODERATE_RISK_FROM) {
    return { score, risk: 'Moderate', interpretation: 'Moderate fall risk: apply standard fall-prevention measures.' };
  }
  return { score, risk: 'Low', interpretation: 'Low fall risk: good basic nursing care.' };
}

// ------------------------------------------------------------------ Braden Scale

export type BradenItem = 'sensoryPerception' | 'moisture' | 'activity' | 'mobility' | 'nutrition' | 'frictionShear';

export type BradenInput = Record<BradenItem, number>;

export const BRADEN_ITEMS: { key: BradenItem; label: string; options: string[] }[] = [
  {
    key: 'sensoryPerception',
    label: 'Sensory perception',
    options: ['Completely limited', 'Very limited', 'Slightly limited', 'No impairment'],
  },
  {
    key: 'moisture',
    label: 'Moisture',
    options: ['Constantly moist', 'Very moist', 'Occasionally moist', 'Rarely moist'],
  },
  { key: 'activity', label: 'Activity', options: ['Bedfast', 'Chairfast', 'Walks occasionally', 'Walks frequently'] },
  {
    key: 'mobility',
    label: 'Mobility',
    options: ['Completely immobile', 'Very limited', 'Slightly limited', 'No limitation'],
  },
  { key: 'nutrition', label: 'Nutrition', options: ['Very poor', 'Probably inadequate', 'Adequate', 'Excellent'] },
  {
    key: 'frictionShear',
    label: 'Friction and shear',
    options: ['Problem', 'Potential problem', 'No apparent problem'],
  },
];

export function bradenScore(input: BradenInput): RiskScore {
  for (const item of BRADEN_ITEMS) {
    const value = input[item.key];
    if (!Number.isInteger(value) || value < 1 || value > item.options.length) {
      throw new RangeError(`${item.label} must be between 1 and ${item.options.length}.`);
    }
  }
  const score = BRADEN_ITEMS.reduce((sum, item) => sum + input[item.key], 0);
  if (score <= 9) {
    return {
      score,
      risk: 'VeryHigh',
      interpretation:
        'Very high pressure-injury risk: two-hourly repositioning, pressure-redistribution mattress, skin checks every shift.',
    };
  }
  if (score <= 12) {
    return {
      score,
      risk: 'High',
      interpretation: 'High pressure-injury risk: two-hourly repositioning and a pressure-relieving surface.',
    };
  }
  if (score <= 14) {
    return {
      score,
      risk: 'Moderate',
      interpretation: 'Moderate pressure-injury risk: turning schedule and heel protection.',
    };
  }
  if (score <= 18) {
    return { score, risk: 'Low', interpretation: 'Mild pressure-injury risk: encourage mobility and keep skin dry.' };
  }
  return { score, risk: 'None', interpretation: 'No significant pressure-injury risk.' };
}

// ------------------------------------------------------------------ Pain

export type PainScaleKind = 'Numeric' | 'WongBakerFaces' | 'Flacc';

export const PAIN_SCALES: { value: PainScaleKind; label: string; hint: string }[] = [
  { value: 'Numeric', label: 'Numeric (0–10)', hint: 'Adults who can tell their pain' },
  { value: 'WongBakerFaces', label: 'Wong-Baker faces', hint: 'Children and low literacy (even scores only)' },
  { value: 'Flacc', label: 'FLACC', hint: 'Infants and patients who cannot speak' },
];

export function painScore(score: number): RiskScore {
  if (!Number.isInteger(score) || score < 0 || score > 10) {
    throw new RangeError('Pain scores range from 0 to 10.');
  }
  if (score === 0) {
    return { score, risk: 'None', interpretation: 'No pain.' };
  }
  if (score <= 3) {
    return { score, risk: 'Low', interpretation: 'Mild pain: comfort measures; reassess next round.' };
  }
  if (score <= 6) {
    return {
      score,
      risk: 'Moderate',
      interpretation: 'Moderate pain: give prescribed analgesia and reassess within an hour.',
    };
  }
  return {
    score,
    risk: 'High',
    interpretation: 'Severe pain: inform the doctor and reassess within 30 minutes of treatment.',
  };
}

/** Scores the chosen pain scale allows (Wong-Baker faces are 0, 2, 4, 6, 8, 10). */
export function painScoreOptions(scale: PainScaleKind): number[] {
  const all = Array.from({ length: 11 }, (_, i) => i);
  return scale === 'WongBakerFaces' ? all.filter((n) => n % 2 === 0) : all;
}
