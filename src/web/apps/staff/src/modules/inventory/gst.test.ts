import { describe, expect, it } from 'vitest';
import {
  exclusiveTotals,
  gstFromInclusive,
  gstOnExclusive,
  gstStateName,
  isInterState,
  isStandardGstRate,
  priceExclusive,
  priceInclusive,
  roundToRupee,
} from './gst';

// Expected values are worked out with the server's GstCalculator rules (C# decimal, half away from zero).
describe('GST calculator mirror', () => {
  it('carves GST out of MRP and splits CGST/SGST with the odd paisa on SGST', () => {
    const line = priceInclusive(10, 12.5, 0, 12);
    expect(line.gross).toBe(125);
    expect(line.discount).toBe(0);
    expect(line.tax.taxable).toBe(111.61);
    expect(line.tax.gst).toBe(13.39);
    expect(line.tax.cgst).toBe(6.7);
    expect(line.tax.sgst).toBe(6.69);
    expect(line.tax.igst).toBe(0);
    expect(line.net).toBe(125);
  });

  it('applies the discount before carving out GST', () => {
    const line = priceInclusive(3, 33.33, 10, 5);
    expect(line.gross).toBe(99.99);
    expect(line.discount).toBe(10);
    expect(line.tax.taxable).toBe(85.7);
    expect(line.tax.cgst).toBe(2.15);
    expect(line.tax.sgst).toBe(2.14);
    expect(line.net).toBe(89.99);
  });

  it('prices purchase lines exclusive of GST', () => {
    const intra = priceExclusive(10, 45.5, 5, 12, false);
    expect(intra.gross).toBe(455);
    expect(intra.discount).toBe(22.75);
    expect(intra.tax.taxable).toBe(432.25);
    expect(intra.tax.gst).toBe(51.87);
    expect(intra.tax.cgst).toBe(25.94);
    expect(intra.tax.sgst).toBe(25.93);
    expect(intra.net).toBe(484.12);

    const inter = priceExclusive(10, 45.5, 5, 12, true);
    expect(inter.tax.igst).toBe(51.87);
    expect(inter.tax.cgst).toBe(0);
    expect(inter.tax.sgst).toBe(0);
    expect(inter.net).toBe(484.12);
  });

  it('keeps half-paisa amounts exact', () => {
    expect(priceExclusive(1, 1.005, 0, 0, false).gross).toBe(1.01);
    const tiny = gstFromInclusive(0.14, 12);
    expect(tiny.taxable).toBe(0.13);
    expect(tiny.gst).toBe(0.01);
    expect(tiny.cgst).toBe(0.01);
    expect(tiny.sgst).toBe(0);
    expect(gstOnExclusive(1, 5, false)).toEqual({ taxable: 1, gst: 0.05, cgst: 0.03, sgst: 0.02, igst: 0, total: 1.05 });
  });

  it('rounds invoice totals to the rupee with a round-off', () => {
    expect(roundToRupee(484.12)).toEqual({ rounded: 484, roundOff: -0.12 });
    expect(roundToRupee(10.5)).toEqual({ rounded: 11, roundOff: 0.5 });
    expect(roundToRupee(-10.5)).toEqual({ rounded: -11, roundOff: -0.5 });
  });

  it('totals purchase documents line by line', () => {
    const totals = exclusiveTotals([priceExclusive(10, 45.5, 5, 12, false), priceExclusive(2, 99.99, 0, 5, false)]);
    expect(totals.gross).toBe(654.98);
    expect(totals.discount).toBe(22.75);
    expect(totals.taxable).toBe(632.23);
    expect(totals.cgst).toBe(30.94);
    expect(totals.sgst).toBe(30.93);
    expect(totals.igst).toBe(0);
    expect(totals.net).toBe(694);
    expect(totals.roundOff).toBe(-0.1);
  });

  it('decides IGST from the GSTIN state codes like the server', () => {
    expect(isInterState('27AAACN1234F1Z5', '27AAACH9876K1Z2')).toBe(false);
    expect(isInterState('29AAACN1234F1Z5', '27AAACH9876K1Z2')).toBe(true);
    expect(isInterState(null, '27AAACH9876K1Z2')).toBe(false);
    expect(isInterState('29AAACN1234F1Z5', '')).toBe(false);
    expect(gstStateName('27AAACN1234F1Z5')).toBe('Maharashtra (27)');
    expect(gstStateName(null)).toBeNull();
  });

  it('knows the notified GST slabs', () => {
    expect(isStandardGstRate(0.25)).toBe(true);
    expect(isStandardGstRate(40)).toBe(true);
    expect(isStandardGstRate(10)).toBe(false);
  });
});
