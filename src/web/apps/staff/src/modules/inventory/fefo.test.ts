import { describe, expect, it } from 'vitest';
import { allocateFefo, allocateFromBatch, batchProblem, batchProblemText, dispensingShelfLifeDays, fefoOrder, usableQuantity, type FefoBatch } from './fefo';

function batch(id: string, expiryDate: string | null, available: number, extra: Partial<FefoBatch> = {}): FefoBatch {
  return { batchId: id, batchNumber: id.toUpperCase(), expiryDate, available, isExpired: false, isBlocked: false, daysToExpiry: expiryDate ? 200 : null, ...extra };
}

const batches: FefoBatch[] = [
  batch('a', '2027-03-31', 5, { daysToExpiry: 186 }),
  batch('b', '2026-12-31', 10, { daysToExpiry: 96 }),
  batch('expired', '2026-09-01', 50, { isExpired: true, daysToExpiry: -25 }),
  batch('blocked', '2026-10-31', 50, { isBlocked: true, blockReason: 'Recall notice 14/2026', daysToExpiry: 35 }),
  batch('empty', '2026-11-30', 0, { daysToExpiry: 65 }),
  batch('none', null, 100),
];

describe('FEFO suggestion', () => {
  it('takes the earliest expiry first and never expired, blocked or empty batches', () => {
    const plan = allocateFefo(12, batches);
    expect(plan.allocations.map((a) => [a.batchId, a.quantity])).toEqual([
      ['b', 10],
      ['a', 2],
    ]);
    expect(plan.allocated).toBe(12);
    expect(plan.shortfall).toBe(0);
  });

  it('uses non-expiring stock last', () => {
    expect(fefoOrder(batches).map((b) => b.batchId)).toEqual(['b', 'a', 'none']);
    expect(allocateFefo(20, batches).allocations.map((a) => [a.batchId, a.quantity])).toEqual([
      ['b', 10],
      ['a', 5],
      ['none', 5],
    ]);
  });

  it('skips batches that expire before the course ends', () => {
    const plan = allocateFefo(8, batches, { minimumShelfLifeDays: 100 });
    expect(plan.allocations.map((a) => [a.batchId, a.quantity])).toEqual([
      ['a', 5],
      ['none', 3],
    ]);
    expect(batchProblem(batches[1]!, 100)).toBe('short-expiry');
  });

  it('uses the picked batch first', () => {
    expect(allocateFefo(3, batches, { preferredBatchId: 'a' }).allocations).toEqual([{ batchId: 'a', batchNumber: 'A', expiryDate: '2027-03-31', quantity: 3 }]);
  });

  it('reports the shortfall when stock runs out', () => {
    const plan = allocateFefo(200, batches);
    expect(plan.allocated).toBe(115);
    expect(plan.shortfall).toBe(85);
    expect(allocateFefo(0, batches).allocations).toEqual([]);
  });

  it('adds decimal quantities exactly', () => {
    const plan = allocateFefo(0.3, [batch('x', '2027-01-01', 0.1), batch('y', '2027-02-01', 0.2)]);
    expect(plan.allocated).toBe(0.3);
    expect(plan.shortfall).toBe(0);
    expect(usableQuantity([batch('x', '2027-01-01', 0.1), batch('y', '2027-02-01', 0.2)])).toBe(0.3);
  });

  it('issues a chosen batch alone, capped at what it holds', () => {
    expect(allocateFromBatch(8, batches[0]!).allocations).toEqual([{ batchId: 'a', batchNumber: 'A', expiryDate: '2027-03-31', quantity: 5 }]);
    expect(allocateFromBatch(8, batches[0]!).shortfall).toBe(3);
    expect(allocateFromBatch(8, batches[3]!).allocations).toEqual([]);
  });

  it('explains unusable batches in plain words', () => {
    expect(batchProblemText('blocked', batches[3]!)).toBe('On hold: Recall notice 14/2026');
    expect(batchProblemText('short-expiry', batches[1]!, 100)).toBe('Expires in 96 days, before the 100-day course ends');
    expect(batchProblem(batches[2]!)).toBe('expired');
    expect(batchProblem(batches[4]!)).toBe('empty');
  });

  it('caps the dispensing shelf-life rule at 30 days', () => {
    expect(dispensingShelfLifeDays(45)).toBe(30);
    expect(dispensingShelfLifeDays(5)).toBe(5);
    expect(dispensingShelfLifeDays(null)).toBe(0);
  });
});
