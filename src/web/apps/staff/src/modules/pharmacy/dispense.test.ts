import { describe, expect, it } from 'vitest';
import type { FefoBatch } from '@/modules/inventory/fefo';
import {
  controlledItems,
  evaluateSubstitution,
  isGenericEquivalent,
  itemsFromErrorData,
  normalizeDrugText,
  planBatchIssue,
  substitutionProblems,
  suggestedQuantity,
} from './dispense';

const batch = (id: string, expiryDate: string, available: number, daysToExpiry: number, extra: Partial<FefoBatch> = {}): FefoBatch => ({
  batchId: id,
  batchNumber: id.toUpperCase(),
  expiryDate,
  available,
  isExpired: daysToExpiry < 0,
  isBlocked: false,
  daysToExpiry,
  ...extra,
});

const batches = [batch('near', '2026-10-10', 20, 14), batch('far', '2027-06-30', 30, 277), batch('held', '2027-01-31', 99, 127, { isBlocked: true, blockReason: 'Recall' })];

describe('substitution policy', () => {
  it('compares generic names and strengths ignoring case, spaces and punctuation', () => {
    expect(normalizeDrugText('Paracetamol 500 mg')).toBe('paracetamol500mg');
    expect(isGenericEquivalent('Paracetamol', '500 mg', 'PARACETAMOL', '500mg')).toBe(true);
    expect(isGenericEquivalent('Paracetamol', '500 mg', 'Paracetamol', '650 mg')).toBe(false);
    expect(isGenericEquivalent('Paracetamol', null, 'Paracetamol', '650 mg')).toBe(true);
    expect(isGenericEquivalent(null, '500 mg', 'Paracetamol', '500 mg')).toBeNull();
  });

  it('treats the prescribed item and brands of a generic order as no substitution', () => {
    const brand = { itemId: 'crocin', genericName: 'Paracetamol', strength: '500 mg' };
    expect(evaluateSubstitution('crocin', 'Paracetamol', '500 mg', brand)).toBe('none');
    expect(evaluateSubstitution(null, 'Paracetamol', '500mg', brand)).toBe('none');
  });

  it('needs reason and consent to swap brands, and refuses non-equivalents', () => {
    const brand = { itemId: 'calpol', genericName: 'Paracetamol', strength: '500 mg' };
    expect(evaluateSubstitution('crocin', 'Paracetamol', '500 mg', brand)).toBe('substitution');
    expect(evaluateSubstitution('crocin', 'Ibuprofen', '400 mg', brand)).toBe('not-equivalent');
    expect(evaluateSubstitution('crocin', null, null, brand)).toBe('substitution');
    expect(substitutionProblems('substitution', ' ', false)).toHaveLength(2);
    expect(substitutionProblems('substitution', 'Out of stock', true)).toEqual([]);
    expect(substitutionProblems('not-equivalent', 'x', true)).toEqual(['Only a generic equivalent of the same strength can be substituted.']);
  });
});

describe('batch picking for a line', () => {
  it('suggests FEFO batches that last the course', () => {
    const plan = planBatchIssue({ quantity: 25, remaining: 30, batchId: null, minimumShelfLifeDays: 10 }, batches, 'TAB');
    expect(plan.problems).toEqual([]);
    expect(plan.allocation.allocations.map((a) => [a.batchId, a.quantity])).toEqual([
      ['near', 20],
      ['far', 5],
    ]);
  });

  it('skips a batch that expires before a 30-day course ends', () => {
    const plan = planBatchIssue({ quantity: 25, remaining: 30, batchId: null, minimumShelfLifeDays: 30 }, batches, 'TAB');
    expect(plan.allocation.allocations.map((a) => a.batchId)).toEqual(['far']);
  });

  it('explains shortages and over-supply', () => {
    const short = planBatchIssue({ quantity: 60, remaining: 60, batchId: null, minimumShelfLifeDays: 30 }, batches, 'TAB');
    expect(short.problems).toEqual(['Only 30 TAB in stock that lasts the 30-day course. Lower the quantity to supply part now.']);
    const over = planBatchIssue({ quantity: 12, remaining: 10, batchId: null, minimumShelfLifeDays: 0 }, batches, 'TAB');
    expect(over.problems[0]).toBe('Only 10 TAB remain to be supplied on this line.');
    expect(planBatchIssue({ quantity: 0, remaining: 10, batchId: null, minimumShelfLifeDays: 0 }, batches).problems).toEqual(['Enter the quantity to hand over.']);
  });

  it('uses a picked batch alone and refuses unusable ones', () => {
    expect(planBatchIssue({ quantity: 5, remaining: null, batchId: 'far', minimumShelfLifeDays: 0 }, batches).allocation.allocations).toEqual([
      { batchId: 'far', batchNumber: 'FAR', expiryDate: '2027-06-30', quantity: 5 },
    ]);
    expect(planBatchIssue({ quantity: 5, remaining: null, batchId: 'held', minimumShelfLifeDays: 0 }, batches).problems).toEqual(['Batch HELD: On hold: Recall.']);
    expect(planBatchIssue({ quantity: 5, remaining: null, batchId: 'near', minimumShelfLifeDays: 30 }, batches).problems[0]).toMatch(/before the 30-day course ends/);
    expect(planBatchIssue({ quantity: 50, remaining: null, batchId: 'far', minimumShelfLifeDays: 0 }, batches).problems[0]).toMatch(/has only 30/);
  });

  it('waits while batches load', () => {
    expect(planBatchIssue({ quantity: 5, remaining: 10, batchId: null, minimumShelfLifeDays: 0 }, undefined).pending).toBe(true);
  });

  it('suggests the smaller of what is left and what is in stock', () => {
    expect(suggestedQuantity(30, 12.5)).toBe(12.5);
    expect(suggestedQuantity(10, 99)).toBe(10);
  });
});

describe('controlled medicines', () => {
  it('asks for confirmation for H1, X and NDPS items only', () => {
    expect(
      controlledItems([
        { name: 'Tramadol 50', schedule: 'H1', isControlled: true },
        { name: 'Alprazolam 0.25', schedule: 'H1', isControlled: false },
        { name: 'Amoxicillin 500', schedule: 'H', isControlled: false },
        { name: 'Morphine 10', schedule: null, isControlled: false, lineIsControlled: true },
        { name: 'Tramadol 50', schedule: 'H1', isControlled: true },
      ]),
    ).toEqual(['Tramadol 50', 'Alprazolam 0.25', 'Morphine 10']);
  });

  it('reads item names from a server refusal', () => {
    expect(itemsFromErrorData({ items: ['Morphine 10', 3] })).toEqual(['Morphine 10']);
    expect(itemsFromErrorData(null)).toEqual([]);
  });
});
