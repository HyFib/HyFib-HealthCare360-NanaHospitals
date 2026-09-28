import { describe, expect, it } from 'vitest';
import { calculateBill, calculateLine, changeDue, mergeCart, type BillLineInput } from './billCalculator';

let id = 0;
const line = (itemName: string, quantity: number, unitPrice: number, gstRate = 5, priceIncludesGst = true): BillLineInput => ({
  menuItemId: `item-${++id}`,
  itemName,
  quantity,
  unitPrice,
  gstRate,
  priceIncludesGst,
});

// The same cases as the server's CanteenBillCalculatorTests, so the POS preview always equals the saved bill.
describe('canteen GST bill (mirror of the server calculator)', () => {
  it('backs GST out of inclusive prices and splits it into CGST and SGST', () => {
    const bill = calculateBill([line('Tea', 2, 10)]);
    expect(bill.taxableAmount).toBe(19.05);
    expect(bill.totalGst).toBe(0.95);
    expect(bill.cgst).toBe(0.48);
    expect(bill.sgst).toBe(0.47);
    expect(bill.grossAmount).toBe(20);
    expect(bill.roundOff).toBe(0);
    expect(bill.netAmount).toBe(20);
  });

  it('adds GST to exclusive prices', () => {
    const bill = calculateBill([line('Catering tray', 1, 100, 5, false)]);
    expect(bill.taxableAmount).toBe(100);
    expect(bill.cgst).toBe(2.5);
    expect(bill.sgst).toBe(2.5);
    expect(bill.netAmount).toBe(105);
  });

  it('rounds the bill to the nearest rupee with a round-off line', () => {
    const bill = calculateBill([line('Meals', 1, 47, 5, false)]);
    expect(bill.cgst).toBe(1.18);
    expect(bill.sgst).toBe(1.17);
    expect(bill.grossAmount).toBe(49.35);
    expect(bill.roundOff).toBe(-0.35);
    expect(bill.netAmount).toBe(49);
  });

  it('rounds fifty paise up', () => {
    const bill = calculateBill([line('Snack', 1, 10, 5, false)]);
    expect(bill.grossAmount).toBe(10.5);
    expect(bill.roundOff).toBe(0.5);
    expect(bill.netAmount).toBe(11);
  });

  it('taxes each line at its own rate', () => {
    const bill = calculateBill([line('Water 1 L', 1, 20, 18), line('Tender coconut', 1, 50, 0)]);
    expect(bill.lines[0]).toMatchObject({ taxableAmount: 16.95, cgst: 1.53, sgst: 1.52 });
    expect(bill.lines[1]).toMatchObject({ taxableAmount: 50, cgst: 0 });
    expect(bill.taxableAmount).toBe(66.95);
    expect(bill.grossAmount).toBe(70);
  });

  it('adds line totals up to the gross amount', () => {
    const bill = calculateBill([line('Idli', 3, 30), line('Coffee', 2, 15), line('Samosa', 4, 15)]);
    const lineTotal = bill.lines.reduce((sum, l) => sum + Math.round(l.lineTotal * 100), 0) / 100;
    const parts = bill.lines.reduce((sum, l) => sum + Math.round((l.taxableAmount + l.cgst + l.sgst) * 100), 0) / 100;
    expect(lineTotal).toBe(bill.grossAmount);
    expect(parts).toBe(bill.grossAmount);
    expect(bill.grossAmount).toBe(180);
  });

  it('rejects non-positive or fractional quantities', () => {
    expect(() => calculateLine(line('Tea', 0, 10))).toThrow(RangeError);
    expect(() => calculateLine(line('Tea', 1.5, 10))).toThrow(RangeError);
  });

  it('has no floating-point drift on paise amounts', () => {
    const bill = calculateBill([line('Biscuit', 7, 12.3, 18, false)]);
    expect(bill.taxableAmount).toBe(86.1);
    expect(bill.totalGst).toBe(15.5);
    expect(bill.grossAmount).toBe(101.6);
    expect(bill.netAmount).toBe(102);
    expect(bill.roundOff).toBe(0.4);
  });
});

describe('cart helpers', () => {
  it('merges repeated items like the server', () => {
    const merged = mergeCart([
      { menuItemId: 'a', quantity: 1 },
      { menuItemId: 'b', quantity: 2 },
      { menuItemId: 'a', quantity: 3 },
    ]);
    expect(merged).toEqual([
      { menuItemId: 'a', quantity: 4 },
      { menuItemId: 'b', quantity: 2 },
    ]);
  });

  it('works out the change for cash', () => {
    expect(changeDue(49, 100)).toBe(51);
    expect(changeDue(49, 20)).toBe(0);
    expect(changeDue(102, 200.5)).toBe(98.5);
  });
});
