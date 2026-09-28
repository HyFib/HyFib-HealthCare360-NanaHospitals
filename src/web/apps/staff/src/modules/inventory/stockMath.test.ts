import { describe, expect, it } from 'vitest';
import { countVariance, summarizeCount, varianceValue } from './stockMath';

describe('stock count arithmetic', () => {
  it('works out differences and their value exactly', () => {
    expect(countVariance(100, 97)).toBe(-3);
    expect(countVariance(0.3, 0.1)).toBe(-0.2);
    expect(varianceValue(-3, 2.345)).toBe(-7.04);
    expect(varianceValue(2, 0.005)).toBe(0.01);
  });

  it('summarises a count sheet', () => {
    expect(
      summarizeCount([
        { lineId: 'a', systemQuantity: 100, unitCost: 2.5, counted: 97 },
        { lineId: 'b', systemQuantity: 10, unitCost: 12, counted: 11 },
        { lineId: 'c', systemQuantity: 5, unitCost: 1, counted: 5 },
        { lineId: 'd', systemQuantity: 8, unitCost: 1, counted: null },
      ]),
    ).toEqual({ counted: 3, total: 4, withDifference: 2, netValue: 4.5, shortValue: 7.5 });
  });
});
