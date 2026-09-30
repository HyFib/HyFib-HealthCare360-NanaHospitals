import { describe, expect, it } from 'vitest';
import {
  explainGstSplit,
  grnLineProblem,
  grnUnitEconomics,
  isShortExpiry,
  previewLine,
  previewTotals,
  tracksExpiry,
  type GrnLineDraft,
} from './gstPreview';

const MH_VENDOR = '27AAACN1234F1Z5';
const KA_VENDOR = '29AAACN1234F1Z5';
const MH_HOSPITAL = '27AAACH9876K1Z2';

describe('purchase GST preview', () => {
  it('prices order lines exclusive of GST with CGST + SGST within the state', () => {
    const line = previewLine({ quantity: 10, rate: 45.5, discountPercent: 5, gstRate: 12 }, false);
    expect(line.tax).toEqual({ taxable: 432.25, gst: 51.87, cgst: 25.94, sgst: 25.93, igst: 0, total: 484.12 });
  });

  it('uses IGST between states and rounds the total to the rupee', () => {
    const totals = previewTotals(
      [
        { quantity: 10, rate: 45.5, discountPercent: 5, gstRate: 12 },
        { quantity: 2, rate: 99.99, discountPercent: null, gstRate: 5 },
      ],
      true,
    );
    expect(totals.cgst).toBe(0);
    expect(totals.sgst).toBe(0);
    expect(totals.igst).toBe(61.87);
    expect(totals.net).toBe(694);
    expect(totals.roundOff).toBe(-0.1);
  });

  it('treats blank figures as zero while typing', () => {
    expect(previewLine({ quantity: null, rate: 10, discountPercent: null, gstRate: null }, false).net).toBe(0);
  });

  it('explains the split in plain words', () => {
    expect(explainGstSplit(MH_VENDOR, MH_HOSPITAL)).toMatchObject({
      interState: false,
      known: true,
      headline: 'CGST + SGST (same state)',
    });
    const inter = explainGstSplit(KA_VENDOR, MH_HOSPITAL);
    expect(inter.interState).toBe(true);
    expect(inter.detail).toContain('Karnataka (29)');
    expect(explainGstSplit(null, MH_HOSPITAL)).toMatchObject({ interState: false, known: true });
    expect(explainGstSplit(KA_VENDOR, null)).toMatchObject({ interState: false, known: false });
  });
});

describe('goods receipt line checks', () => {
  const base: GrnLineDraft = {
    itemName: 'Amoxicillin 500',
    itemType: 'Drug',
    unitsPerPurchaseUnit: 10,
    batchNumber: 'AMX2611',
    manufactureDate: '2026-05-01',
    expiryDate: '2028-04-30',
    quantity: 20,
    freeQuantity: 2,
    rate: 60,
    mrp: 95,
    discountPercent: 0,
    gstRate: 12,
    acceptShortExpiry: false,
  };

  it('accepts a complete medicine line', () => {
    expect(grnLineProblem(base, '2026-09-26')).toBeNull();
  });

  it('mirrors the server receiving rules', () => {
    expect(grnLineProblem({ ...base, quantity: 0, freeQuantity: 0 }, '2026-09-26')).toBe(
      'Enter the received quantity.',
    );
    expect(grnLineProblem({ ...base, batchNumber: ' ' }, '2026-09-26')).toMatch(/batch number/);
    expect(grnLineProblem({ ...base, expiryDate: null }, '2026-09-26')).toBe('Enter the expiry date.');
    expect(grnLineProblem({ ...base, expiryDate: '2026-09-26' }, '2026-09-26')).toMatch(/expired/);
    expect(grnLineProblem({ ...base, expiryDate: '2026-11-30' }, '2026-09-26')).toMatch(/within 90 days/);
    expect(grnLineProblem({ ...base, expiryDate: '2026-11-30', acceptShortExpiry: true }, '2026-09-26')).toBeNull();
    expect(grnLineProblem({ ...base, manufactureDate: '2028-05-01' }, '2026-09-26')).toMatch(/manufacturing date/);
    expect(grnLineProblem({ ...base, mrp: 0 }, '2026-09-26')).toBe('Enter the MRP printed on the pack.');
    expect(grnLineProblem({ ...base, rate: 96 }, '2026-09-26')).toMatch(/above the MRP/);
    expect(
      grnLineProblem({ ...base, itemType: 'Linen', batchNumber: '', expiryDate: null, mrp: 0 }, '2026-09-26'),
    ).toBeNull();
    expect(isShortExpiry('2026-11-30', '2026-09-26')).toBe(true);
    expect(isShortExpiry('2027-11-30', '2026-09-26')).toBe(false);
    expect(tracksExpiry('Spare')).toBe(false);
  });

  it('spreads the cost over free goods and converts MRP to the base unit', () => {
    // 20 strips + 2 free strips of 10 tablets, taxable ₹1,200 → 220 tablets at ₹5.4545 each; MRP ₹95 a strip = ₹9.5 a tablet.
    expect(grnUnitEconomics(20, 2, 10, 1200, 95)).toEqual({ baseQuantity: 220, unitCost: 5.4545, unitMrp: 9.5 });
    expect(grnUnitEconomics(0, 0, 10, 0, 95).unitCost).toBe(0);
  });
});
