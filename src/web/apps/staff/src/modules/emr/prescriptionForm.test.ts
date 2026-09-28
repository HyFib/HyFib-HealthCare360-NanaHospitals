import { describe, expect, it } from 'vitest';
import {
  appendLines,
  applyDefaults,
  autoQuantity,
  duplicateGenerics,
  effectiveQuantity,
  emptyLine,
  fromSavedItem,
  rxFormSchema,
  toItemRequest,
  toRequestBody,
  type RxLineValues,
} from './prescriptionForm';

function line(partial: Partial<RxLineValues>): RxLineValues {
  return { ...emptyLine(), ...partial };
}

const paracetamol = line({ drugName: 'Dolo 650', genericName: 'Paracetamol', strength: '650 mg', dosageForm: 'Tablet', dose: '1 tab', frequency: 'TDS', durationDays: 3 });

describe('quantities', () => {
  it('calculates solid-form quantities and respects an explicit override', () => {
    expect(autoQuantity(paracetamol)).toBe(9);
    expect(effectiveQuantity({ ...paracetamol, quantity: 10 })).toBe(10);
  });

  it('needs an explicit quantity for liquids', () => {
    const syrup = line({ drugName: 'Crocin syrup', dose: '5 ml', frequency: 'TDS', durationDays: 3 });
    expect(effectiveQuantity(syrup)).toBeNull();
    const result = rxFormSchema.safeParse({ lines: [syrup], advice: '', notes: '', refillsAllowed: 0 });
    expect(result.success).toBe(false);
    expect(result.error?.issues.some((issue) => issue.path.join('.') === 'lines.0.quantity')).toBe(true);
  });
});

describe('validation', () => {
  it('accepts a complete prescription', () => {
    expect(rxFormSchema.safeParse({ lines: [paracetamol], advice: 'Plenty of fluids', notes: '', refillsAllowed: 0 }).success).toBe(true);
  });

  it('asks for the custom frequency text', () => {
    const custom = { ...paracetamol, frequency: 'Custom' as const, customFrequency: '', quantity: 6 };
    const result = rxFormSchema.safeParse({ lines: [custom], advice: '', notes: '', refillsAllowed: 0 });
    expect(result.error?.issues.map((issue) => issue.path.join('.'))).toContain('lines.0.customFrequency');
  });
});

describe('mapping', () => {
  it('builds the item request', () => {
    const request = toItemRequest({ ...paracetamol, instructions: ' After food ', isControlledDrug: true, controlledSchedule: 'h1' });
    expect(request).toMatchObject({ drugName: 'Dolo 650', genericName: 'Paracetamol', frequency: 'TDS', durationDays: 3, quantity: null, instructions: 'After food', controlledSchedule: 'H1' });
  });

  it('sends one day for STAT doses and drops blank rows', () => {
    const body = toRequestBody({ lines: [{ ...paracetamol, frequency: 'STAT', durationDays: null }, emptyLine()], advice: '', notes: '', refillsAllowed: 1 });
    expect(body.items).toHaveLength(1);
    expect(body.items[0]?.durationDays).toBe(1);
    expect(body.advice).toBeNull();
  });

  it('keeps calculated quantities automatic when reloading a draft', () => {
    const saved = { ...toItemRequest(paracetamol), quantity: 9, durationDays: 3 };
    expect(fromSavedItem(saved).quantity).toBeNull();
    expect(fromSavedItem({ ...saved, quantity: 12 }).quantity).toBe(12);
  });

  it('applies favourite defaults and marks controlled medicines', () => {
    const next = applyDefaults(emptyLine(), { drugName: 'Alprazolam 0.25', genericName: 'Alprazolam', dose: '1 tab', frequency: 'HS', durationDays: 7, isControlledDrug: true });
    expect(next).toMatchObject({ drugName: 'Alprazolam 0.25', frequency: 'HS', durationDays: 7, isControlledDrug: true, controlledSchedule: 'H1', route: 'Oral' });
  });

  it('replaces a trailing blank row when adding medicines', () => {
    const lines = appendLines([paracetamol, emptyLine()], [line({ drugName: 'Pantoprazole 40' })]);
    expect(lines.map((l) => l.drugName)).toEqual(['Dolo 650', 'Pantoprazole 40']);
  });

  it('spots the same generic twice', () => {
    expect(duplicateGenerics([paracetamol, { ...paracetamol, drugName: 'Crocin 500' }, line({ genericName: 'Ibuprofen' })])).toEqual(['paracetamol']);
  });
});
