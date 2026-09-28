import { formatNumber } from '@hc360/core';
import { z } from 'zod';
import { DOSE_FREQUENCIES, quantityToDispense, type DoseFrequency } from './dosing';
import type { PrescribableDrug } from './prescriptionsApi';

/**
 * The prescription editor's form model (pure, unit-tested): one row per medicine, validation that mirrors the
 * server (so signing does not fail late) and mapping to and from the API, favourites, templates and the drug master.
 */

export interface RxLineValues {
  /** Client-side row key (not sent). */
  key: string;
  drugItemId: string | null;
  drugName: string;
  genericName: string;
  strength: string;
  dosageForm: string;
  route: string;
  dose: string;
  frequency: DoseFrequency;
  customFrequency: string;
  durationDays: number | null;
  /** Explicit quantity to dispense; null = calculated from dose × frequency × days. */
  quantity: number | null;
  instructions: string;
  isControlledDrug: boolean;
  controlledSchedule: string;
}

export interface RxFormValues {
  lines: RxLineValues[];
  advice: string;
  notes: string;
  refillsAllowed: number;
}

let counter = 0;

export function newLineKey(): string {
  counter += 1;
  return `rx-${Date.now().toString(36)}-${counter}`;
}

export function emptyLine(): RxLineValues {
  return {
    key: newLineKey(),
    drugItemId: null,
    drugName: '',
    genericName: '',
    strength: '',
    dosageForm: '',
    route: 'Oral',
    dose: '',
    frequency: 'BD',
    customFrequency: '',
    durationDays: 5,
    quantity: null,
    instructions: '',
    isControlledDrug: false,
    controlledSchedule: '',
  };
}

export const emptyRxForm = (): RxFormValues => ({ lines: [emptyLine()], advice: '', notes: '', refillsAllowed: 0 });

export function isBlankLine(line: Pick<RxLineValues, 'drugName' | 'dose'>): boolean {
  return !line.drugName.trim() && !line.dose.trim();
}

/** Quantity the server will calculate (null when it cannot: liquids, SOS…). */
export function autoQuantity(line: RxLineValues): number | null {
  const days = line.frequency === 'STAT' ? 1 : (line.durationDays ?? 0);
  return quantityToDispense(line.dose, line.strength, line.frequency, line.customFrequency, null, days);
}

export function effectiveQuantity(line: RxLineValues): number | null {
  return line.quantity !== null && line.quantity > 0 ? line.quantity : autoQuantity(line);
}

const lineSchema = z
  .object({
    key: z.string(),
    drugItemId: z.string().nullable(),
    drugName: z.string().trim().min(1, 'Choose or type the medicine.').max(200, 'Keep the name under 200 characters.'),
    genericName: z.string().max(300),
    strength: z.string().max(64, 'Keep the strength under 64 characters.'),
    dosageForm: z.string().max(64),
    route: z.string().max(32),
    dose: z.string().trim().min(1, 'Enter the dose (e.g. 1 tab, 500 mg, 5 ml).').max(64),
    frequency: z.enum(DOSE_FREQUENCIES as unknown as [DoseFrequency, ...DoseFrequency[]]),
    customFrequency: z.string().max(64),
    durationDays: z.number().int('Use whole days.').min(1, 'At least 1 day.').max(365, 'At most 365 days.').nullable(),
    quantity: z.number().min(0).max(10000, 'Check the quantity.').nullable(),
    instructions: z.string().max(500),
    isControlledDrug: z.boolean(),
    controlledSchedule: z.string().max(8),
  })
  .superRefine((line, ctx) => {
    if (line.frequency === 'Custom' && !line.customFrequency.trim()) {
      ctx.addIssue({ code: 'custom', path: ['customFrequency'], message: 'Write the frequency (e.g. 1-0-1 or 8 hourly).' });
    }
    if (line.frequency !== 'STAT' && line.durationDays === null) {
      ctx.addIssue({ code: 'custom', path: ['durationDays'], message: 'Enter the number of days.' });
    }
    if (effectiveQuantity(line) === null) {
      ctx.addIssue({ code: 'custom', path: ['quantity'], message: 'Enter the quantity to dispense (it cannot be calculated for this dose).' });
    }
  });

export const rxFormSchema = z.object({
  lines: z.array(lineSchema).min(1, 'Add at least one medicine.').max(30, 'At most 30 medicines per prescription.'),
  advice: z.string().max(2000, 'Keep the advice under 2,000 characters.'),
  notes: z.string().max(1000, 'Keep the note under 1,000 characters.'),
  refillsAllowed: z.number().int().min(0).max(12, 'At most 12 refills.'),
});

export interface ItemRequest {
  drugItemId: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string;
  frequency: DoseFrequency;
  customFrequency: string | null;
  dosesPerDay: number | null;
  durationDays: number;
  quantity: number | null;
  instructions: string | null;
  isControlledDrug: boolean;
  controlledSchedule: string | null;
}

const text = (value: string) => value.trim() || null;

export function toItemRequest(line: RxLineValues): ItemRequest {
  return {
    drugItemId: line.drugItemId,
    drugName: line.drugName.trim(),
    genericName: text(line.genericName),
    strength: text(line.strength),
    dosageForm: text(line.dosageForm),
    route: text(line.route),
    dose: line.dose.trim(),
    frequency: line.frequency,
    customFrequency: line.frequency === 'Custom' ? text(line.customFrequency) : null,
    dosesPerDay: null,
    durationDays: line.frequency === 'STAT' ? 1 : (line.durationDays ?? 1),
    quantity: line.quantity !== null && line.quantity > 0 ? line.quantity : null,
    instructions: text(line.instructions),
    isControlledDrug: line.isControlledDrug,
    controlledSchedule: line.isControlledDrug ? text(line.controlledSchedule)?.toUpperCase() ?? null : null,
  };
}

/** True when the form holds anything worth keeping (a medicine, advice or a note). */
export function hasRxContent(values: RxFormValues): boolean {
  return values.lines.some((line) => !isBlankLine(line)) || Boolean(values.advice.trim()) || Boolean(values.notes.trim());
}

/** Same prescription content (row keys, blank rows and surrounding spaces ignored). */
export function sameRxContent(a: RxFormValues, b: RxFormValues): boolean {
  return JSON.stringify(toRequestBody(a)) === JSON.stringify(toRequestBody(b));
}

/** Request body parts shared by create and update (blank rows are dropped). */
export function toRequestBody(values: RxFormValues) {
  return {
    notes: text(values.notes),
    advice: text(values.advice),
    refillsAllowed: values.refillsAllowed,
    items: values.lines.filter((line) => !isBlankLine(line)).map(toItemRequest),
  };
}

export interface SavedItem {
  drugItemId: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string;
  frequency: DoseFrequency;
  customFrequency: string | null;
  durationDays: number;
  quantity: number;
  instructions: string | null;
  isControlledDrug: boolean;
  controlledSchedule: string | null;
}

/** Form row from a saved line; a quantity equal to the calculated one stays "automatic" so edits recalculate it. */
export function fromSavedItem(item: SavedItem): RxLineValues {
  const line: RxLineValues = {
    key: newLineKey(),
    drugItemId: item.drugItemId,
    drugName: item.drugName,
    genericName: item.genericName ?? '',
    strength: item.strength ?? '',
    dosageForm: item.dosageForm ?? '',
    route: item.route ?? '',
    dose: item.dose,
    frequency: item.frequency,
    customFrequency: item.customFrequency ?? '',
    durationDays: item.durationDays,
    quantity: null,
    instructions: item.instructions ?? '',
    isControlledDrug: item.isControlledDrug,
    controlledSchedule: item.controlledSchedule ?? '',
  };
  return autoQuantity(line) === item.quantity ? line : { ...line, quantity: item.quantity > 0 ? item.quantity : null };
}

/** Editor values from a saved draft (an empty draft still gets one blank row to type into). */
export function toRxFormValues(saved: { items: readonly SavedItem[]; advice: string | null; notes: string | null; refillsAllowed: number }): RxFormValues {
  return {
    lines: saved.items.length > 0 ? saved.items.map(fromSavedItem) : [emptyLine()],
    advice: saved.advice ?? '',
    notes: saved.notes ?? '',
    refillsAllowed: saved.refillsAllowed,
  };
}

export interface MedicineDefaults {
  drugItemId?: string | null;
  drugName: string;
  genericName?: string | null;
  strength?: string | null;
  dosageForm?: string | null;
  route?: string | null;
  dose?: string | null;
  frequency?: DoseFrequency | null;
  customFrequency?: string | null;
  durationDays?: number | null;
  instructions?: string | null;
  isControlledDrug?: boolean;
  controlledSchedule?: string | null;
}

/** A saved favourite medicine (FavouriteDto subset). */
export interface FavouriteMedicine {
  label: string;
  drugItemId: string | null;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string | null;
  frequency: DoseFrequency | null;
  customFrequency: string | null;
  durationDays: number | null;
  instructions: string | null;
  isControlledDrug: boolean;
}

export function favouriteDefaults(f: FavouriteMedicine): MedicineDefaults {
  return {
    drugItemId: f.drugItemId,
    drugName: f.label,
    genericName: f.genericName,
    strength: f.strength,
    dosageForm: f.dosageForm,
    route: f.route,
    dose: f.dose,
    frequency: f.frequency,
    customFrequency: f.customFrequency,
    durationDays: f.durationDays,
    instructions: f.instructions,
    isControlledDrug: f.isControlledDrug,
  };
}

/** Favourite built from a prescription row ("save as favourite"). */
export function favouriteFromLine(line: RxLineValues): FavouriteMedicine {
  return {
    label: line.drugName.trim(),
    drugItemId: line.drugItemId,
    genericName: line.genericName.trim() || null,
    strength: line.strength.trim() || null,
    dosageForm: line.dosageForm.trim() || null,
    route: line.route.trim() || null,
    dose: line.dose.trim() || null,
    frequency: line.frequency,
    customFrequency: line.frequency === 'Custom' ? line.customFrequency.trim() || null : null,
    durationDays: line.durationDays,
    instructions: line.instructions.trim() || null,
    isControlledDrug: line.isControlledDrug,
  };
}

/** Fills a row from a favourite, template medicine or drug-master pick, keeping what the defaults do not define. */
export function applyDefaults(line: RxLineValues, defaults: MedicineDefaults): RxLineValues {
  return {
    ...line,
    drugItemId: defaults.drugItemId ?? null,
    drugName: defaults.drugName,
    genericName: defaults.genericName ?? '',
    strength: defaults.strength ?? '',
    dosageForm: defaults.dosageForm ?? line.dosageForm,
    route: defaults.route ?? line.route,
    dose: defaults.dose ?? line.dose,
    frequency: defaults.frequency ?? line.frequency,
    customFrequency: defaults.customFrequency ?? '',
    durationDays: defaults.durationDays ?? line.durationDays,
    quantity: null,
    instructions: defaults.instructions ?? line.instructions,
    isControlledDrug: defaults.isControlledDrug ?? false,
    controlledSchedule: defaults.isControlledDrug ? (defaults.controlledSchedule ?? 'H1') : '',
  };
}

/** Row from a medicine the patient is currently taking ("continue"); unknown frequency text is kept as custom. */
export function fromCurrentMedication(m: { drugName: string; genericName: string | null; strength: string | null; dose: string; frequency: string; durationDays: number }): RxLineValues {
  const known = (DOSE_FREQUENCIES as readonly string[]).includes(m.frequency) && m.frequency !== 'Custom';
  return applyDefaults(emptyLine(), {
    drugName: m.drugName,
    genericName: m.genericName,
    strength: m.strength,
    dose: m.dose,
    frequency: known ? (m.frequency as DoseFrequency) : 'Custom',
    customFrequency: known ? null : m.frequency,
    durationDays: m.durationDays,
  });
}

/** Row defaults from a drug-master pick: the server decides what is controlled (Schedule H1/X or NDPS) and its schedule. */
export function drugMasterDefaults(
  drug: Pick<PrescribableDrug, 'id' | 'name' | 'genericName' | 'strength' | 'dosageForm' | 'isControlled' | 'controlledSchedule'>,
): MedicineDefaults {
  return {
    drugItemId: drug.id,
    drugName: drug.name,
    genericName: drug.genericName,
    strength: drug.strength,
    dosageForm: drug.dosageForm,
    isControlledDrug: drug.isControlled,
    controlledSchedule: drug.controlledSchedule,
  };
}

export interface DrugFlag {
  kind: 'controlled' | 'coldChain';
  label: string;
}

/** How a drug-master medicine is shown in the picker. */
export interface DrugOptionView {
  /** Generic name, strength and form. */
  detail: string;
  /** "In stock: 120 TAB" or "Out of stock"; null when the facility has no pharmacy store (stock unknown). */
  stock: string | null;
  outOfStock: boolean;
  /** Written flags (never colour alone): the controlled-drug schedule and cold-chain storage. */
  flags: DrugFlag[];
}

export function drugOptionView(
  drug: Pick<PrescribableDrug, 'genericName' | 'strength' | 'dosageForm' | 'isControlled' | 'controlledSchedule' | 'isColdChain' | 'baseUnit' | 'available'>,
): DrugOptionView {
  const flags: DrugFlag[] = [];
  if (drug.isControlled) {
    flags.push({ kind: 'controlled', label: `Schedule ${drug.controlledSchedule ?? 'H1'}` });
  }
  if (drug.isColdChain) {
    flags.push({ kind: 'coldChain', label: 'Cold chain' });
  }
  const available = drug.available ?? null;
  const outOfStock = available !== null && available <= 0;
  const stock = available === null ? null : outOfStock ? 'Out of stock' : `In stock: ${formatNumber(available)} ${drug.baseUnit}`;
  return { detail: [drug.genericName, drug.strength, drug.dosageForm].filter(Boolean).join(' · '), stock, outOfStock, flags };
}

/** Row defaults from a speciality template's medicine. */
export function templateMedicineDefaults(m: {
  drugItemId: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string;
  frequency: DoseFrequency;
  customFrequency: string | null;
  durationDays: number;
  instructions: string | null;
}): MedicineDefaults {
  return { ...m };
}

/** Replaces the trailing blank row (if any) with new rows, so picking from favourites never leaves gaps. */
export function appendLines(lines: readonly RxLineValues[], added: readonly RxLineValues[]): RxLineValues[] {
  const kept = lines.filter((line, index) => !(index === lines.length - 1 && isBlankLine(line)));
  return [...kept, ...added];
}

/** Generic names that appear on more than one row (a quick duplicate-therapy hint before the server check). */
export function duplicateGenerics(lines: readonly RxLineValues[]): string[] {
  const seen = new Map<string, number>();
  for (const line of lines) {
    const generic = line.genericName.trim().toLowerCase();
    if (generic) {
      seen.set(generic, (seen.get(generic) ?? 0) + 1);
    }
  }
  return [...seen.entries()].filter(([, count]) => count > 1).map(([generic]) => generic);
}
