/**
 * Prescription dosing helpers (pure, unit-tested). They mirror the server's DoseCalculator so the doctor sees the
 * quantity to dispense and plain-language directions while typing; the server recomputes everything on save.
 */

/** Dosing frequency as written on Indian prescriptions (server enum `DoseFrequency`). */
export type DoseFrequency = 'OD' | 'BD' | 'TDS' | 'QID' | 'HS' | 'SOS' | 'STAT' | 'Custom' | 'Weekly';

export const DOSE_FREQUENCIES: readonly DoseFrequency[] = ['OD', 'BD', 'TDS', 'QID', 'HS', 'SOS', 'STAT', 'Weekly', 'Custom'];

/** Plain-language meaning of each frequency (what the patient reads). */
export const frequencyMeaning: Record<DoseFrequency, string> = {
  OD: 'Once a day',
  BD: 'Twice a day',
  TDS: 'Three times a day',
  QID: 'Four times a day',
  HS: 'At bedtime',
  SOS: 'Only when required',
  STAT: 'Immediately, once',
  Weekly: 'Once a week',
  Custom: 'Custom',
};

/** "BD – twice a day": the abbreviation doctors type, with its meaning so nobody has to guess. */
export function frequencyOptionLabel(frequency: DoseFrequency): string {
  return frequency === 'Custom' ? 'Custom (e.g. 1-0-1, 8 hourly)' : `${frequency} – ${frequencyMeaning[frequency].toLowerCase()}`;
}

export const ROUTES = ['Oral', 'IV', 'IM', 'SC', 'Topical', 'Inhalation', 'Sublingual', 'Rectal', 'Nasal', 'Eye', 'Ear', 'Vaginal', 'Other'] as const;

export const DOSAGE_FORMS = [
  'Tablet',
  'Capsule',
  'Syrup',
  'Suspension',
  'Drops',
  'Injection',
  'Infusion',
  'Ointment',
  'Cream',
  'Gel',
  'Inhaler',
  'Nebulisation',
  'Sachet',
  'Suppository',
  'Other',
] as const;

export const INSTRUCTION_PRESETS = ['After food', 'Before food', 'With food', 'Empty stomach', 'At bedtime', 'Apply locally', 'Dissolve in water'] as const;

export const DURATION_PRESETS = [1, 3, 5, 7, 10, 14, 30] as const;

export type AmountUnit = 'mg' | 'count' | 'ml' | 'other';

export interface DoseAmount {
  value: number;
  unit: AmountUnit;
}

const EPSILON = 1e-9;

function replaceVulgarFractions(text: string): string {
  return text.replace(/1½/g, '1.5').replace(/½/g, '0.5').replace(/¼/g, '0.25').replace(/¾/g, '0.75');
}

const COUNT_UNITS = new Set([
  '',
  'tab',
  'tabs',
  'tablet',
  'tablets',
  'cap',
  'caps',
  'capsule',
  'capsules',
  'sachet',
  'sachets',
  'supp',
  'suppository',
  'suppositories',
  'lozenge',
  'lozenges',
  'no',
  'nos',
]);

/** "1 tab" → 1 count, "500 mg" → 500 mg, "1 g" → 1000 mg, "½" → 0.5 count, "5 ml" → 5 ml. Null when unreadable. */
export function parseDose(dose: string | null | undefined): DoseAmount | null {
  if (!dose || !dose.trim()) {
    return null;
  }
  const text = replaceVulgarFractions(dose.trim().toLowerCase());
  const match = /^(\d+(?:\.\d+)?)(?:\s*\/\s*(\d+))?\s*([a-zµ]+)?/.exec(text);
  if (!match) {
    return null;
  }
  let value = Number(match[1]);
  if (match[2] !== undefined) {
    const denominator = Number(match[2]);
    if (denominator === 0) {
      return null;
    }
    value /= denominator;
  }
  const unit = match[3] ?? '';
  switch (unit) {
    case 'mg':
      return { value, unit: 'mg' };
    case 'g':
    case 'gm':
    case 'gram':
    case 'grams':
      return { value: value * 1000, unit: 'mg' };
    case 'mcg':
    case 'µg':
    case 'microgram':
    case 'micrograms':
      return { value: value / 1000, unit: 'mg' };
    case 'ml':
      return { value, unit: 'ml' };
    default:
      return COUNT_UNITS.has(unit) ? { value, unit: 'count' } : { value, unit: 'other' };
  }
}

/** "1-0-1" → 2 doses a day, "2-0-2" → 4, "8 hourly" → 3, "alternate day" → 0.5. Null when unreadable. */
export function parseCustomFrequency(text: string | null | undefined): number | null {
  if (!text || !text.trim()) {
    return null;
  }
  const value = replaceVulgarFractions(text.trim().toLowerCase());
  if (/^\s*(\d+(?:\.\d+)?)(\s*-\s*\d+(?:\.\d+)?){1,3}\s*$/.test(value)) {
    const total = value.split('-').reduce((sum, part) => sum + Number(part.trim()), 0);
    return total > 0 ? total : null;
  }
  const hourly = /(?:q\s*|every\s*)?(\d{1,2})\s*(?:st|nd|rd|th)?\s*(?:h|hr|hrs|hour|hours|hourly)\b/.exec(value);
  if (hourly) {
    const hours = Number(hourly[1]);
    if (hours >= 1 && hours <= 24) {
      return 24 / hours;
    }
  }
  if (value.includes('alternate') || value.includes('every other day')) {
    return 0.5;
  }
  if (value.includes('thrice weekly') || value.includes('three times a week')) {
    return 3 / 7;
  }
  if (value.includes('twice weekly') || value.includes('twice a week')) {
    return 2 / 7;
  }
  if (value.includes('week')) {
    return 1 / 7;
  }
  switch (value) {
    case 'once daily':
    case 'once a day':
    case 'daily':
      return 1;
    case 'twice daily':
    case 'twice a day':
      return 2;
    case 'thrice daily':
    case 'three times a day':
      return 3;
    case 'four times a day':
      return 4;
    default:
      return null;
  }
}

/** Administrations per day; null for SOS (no fixed schedule) and unreadable custom text. */
export function dosesPerDay(frequency: DoseFrequency, customFrequency?: string | null, explicitPerDay?: number | null): number | null {
  if (explicitPerDay !== null && explicitPerDay !== undefined && explicitPerDay > 0) {
    return explicitPerDay;
  }
  switch (frequency) {
    case 'OD':
    case 'HS':
    case 'STAT':
      return 1;
    case 'BD':
      return 2;
    case 'TDS':
      return 3;
    case 'QID':
      return 4;
    case 'Weekly':
      return 1 / 7;
    case 'Custom':
      return parseCustomFrequency(customFrequency);
    default:
      return null;
  }
}

function toMg(value: number, unit: string): number {
  if (unit === 'g' || unit === 'gm') {
    return value * 1000;
  }
  if (unit === 'mcg' || unit === 'µg') {
    return value / 1000;
  }
  return value;
}

/** Per-unit strengths in mg ("400 mg + 325 mg" → [400, 325]); empty for liquids, IU and percentages. */
export function parseStrengthMg(strength: string | null | undefined): number[] {
  if (!strength || !strength.trim()) {
    return [];
  }
  const text = strength.toLowerCase();
  if (text.includes('ml')) {
    return [];
  }
  const parts = [...text.matchAll(/(\d+(?:\.\d+)?)\s*(mg|mcg|µg|gm|g|iu|%)?/g)].map((m) => ({ value: Number(m[1]), unit: m[2] ?? null }));
  if (parts.length === 0 || parts.some((p) => p.unit === 'iu' || p.unit === '%')) {
    return [];
  }
  return parts.map((part, index) => {
    const unit = part.unit ?? parts.slice(index + 1).find((p) => p.unit !== null)?.unit ?? 'mg';
    return toMg(part.value, unit);
  });
}

function ceilSafe(value: number): number {
  return Math.ceil(value - EPSILON);
}

function isMultipleOf(value: number, step: number): boolean {
  if (step <= 0) {
    return false;
  }
  const ratio = value / step;
  return Math.abs(ratio - Math.round(ratio)) < 1e-6;
}

/**
 * Units to dispense for solid forms (units per dose × doses a day × days, rounded up). Null when it cannot be
 * computed safely (liquids, SOS, unreadable dose) — the doctor then enters the quantity.
 */
export function quantityToDispense(
  dose: string,
  strength: string | null | undefined,
  frequency: DoseFrequency,
  customFrequency: string | null | undefined,
  explicitPerDay: number | null | undefined,
  durationDays: number,
): number | null {
  const amount = parseDose(dose);
  if (!amount || !(durationDays > 0)) {
    return null;
  }
  let unitsPerDose: number | null = null;
  if (amount.unit === 'count') {
    unitsPerDose = amount.value;
  } else if (amount.unit === 'mg') {
    const strengths = parseStrengthMg(strength);
    const unitMg = strengths.length === 1 ? strengths[0]! : 0;
    if (unitMg > 0 && isMultipleOf(amount.value, unitMg / 2)) {
      unitsPerDose = amount.value / unitMg;
    }
  }
  if (unitsPerDose === null || !(unitsPerDose > 0)) {
    return null;
  }
  if (frequency === 'STAT') {
    return ceilSafe(unitsPerDose);
  }
  if (frequency === 'Weekly' && (explicitPerDay === null || explicitPerDay === undefined || explicitPerDay <= 0)) {
    return ceilSafe(ceilSafe(durationDays / 7) * unitsPerDose);
  }
  const perDay = dosesPerDay(frequency, customFrequency, explicitPerDay);
  return perDay !== null && perDay > 0 ? ceilSafe(unitsPerDose * perDay * durationDays) : null;
}

/** "5 days", "1 day", "4 weeks" (whole weeks read better on long courses). */
export function durationText(days: number | null | undefined): string {
  if (days === null || days === undefined || !(days > 0)) {
    return '';
  }
  if (days >= 14 && days % 7 === 0) {
    const weeks = days / 7;
    return `${weeks} weeks`;
  }
  return days === 1 ? '1 day' : `${days} days`;
}

export interface DirectionsInput {
  dose: string;
  frequency: DoseFrequency;
  customFrequency?: string | null;
  durationDays?: number | null;
  route?: string | null;
  instructions?: string | null;
}

/** One-line directions: "1 tab · twice a day (BD) · 5 days · after food". */
export function directionsText(line: DirectionsInput): string {
  const frequency =
    line.frequency === 'Custom'
      ? line.customFrequency?.trim() || 'as directed'
      : `${frequencyMeaning[line.frequency].toLowerCase()} (${line.frequency})`;
  const parts = [line.dose.trim(), frequency];
  if (line.frequency !== 'STAT') {
    const duration = durationText(line.durationDays);
    if (duration) {
      parts.push(duration);
    }
  }
  if (line.route && line.route !== 'Oral') {
    parts.push(line.route);
  }
  if (line.instructions?.trim()) {
    parts.push(line.instructions.trim().replace(/^./, (c) => c.toLowerCase()));
  }
  return parts.filter(Boolean).join(' · ');
}

/** Unit word for the quantity field ("tabs", "caps", "units"). */
export function quantityUnit(dosageForm: string | null | undefined, dose: string | null | undefined): string {
  const form = (dosageForm ?? '').toLowerCase();
  if (form.startsWith('tab')) {
    return 'tabs';
  }
  if (form.startsWith('cap')) {
    return 'caps';
  }
  if (form.startsWith('sachet')) {
    return 'sachets';
  }
  if (form.startsWith('supp')) {
    return 'suppositories';
  }
  if (['syrup', 'suspension', 'drops'].some((f) => form.startsWith(f))) {
    return 'bottles';
  }
  if (['ointment', 'cream', 'gel'].some((f) => form.startsWith(f))) {
    return 'tubes';
  }
  if (form.startsWith('inhaler')) {
    return 'inhalers';
  }
  if (form.startsWith('inj') || form.startsWith('infusion')) {
    return 'vials/ampoules';
  }
  const amount = parseDose(dose);
  return amount?.unit === 'count' ? 'units' : 'packs';
}

/** Schedule H1 / X, written the way the Drugs & Cosmetics Rules name them. */
export const CONTROLLED_SCHEDULES = ['H1', 'X'] as const;
export type ControlledSchedule = (typeof CONTROLLED_SCHEDULES)[number];
