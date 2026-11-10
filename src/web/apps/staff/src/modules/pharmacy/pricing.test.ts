import { describe, expect, it } from 'vitest';
import type { PharmacyPaymentDto } from './api';
import { ipIssueAmount, priceSaleLine, refundFor, refundSplit, refundWords, saleTotals } from './pricing';

// Worked with the server's SalePricing / GstCalculator rules (C# decimal, half away from zero).
describe('pharmacy bill preview', () => {
  it('prices a line at MRP with GST carved out and split CGST/SGST', () => {
    expect(priceSaleLine(12.5, 10, 12, 0)).toEqual({
      gross: 125,
      discount: 0,
      taxable: 111.61,
      cgst: 6.7,
      sgst: 6.69,
      net: 125,
    });
  });

  it('takes the discount off the MRP value first', () => {
    expect(priceSaleLine(33.33, 3, 5, 10)).toEqual({
      gross: 99.99,
      discount: 10,
      taxable: 85.7,
      cgst: 2.15,
      sgst: 2.14,
      net: 89.99,
    });
  });

  it('keeps the payable exact when the GST rate is not known', () => {
    const line = priceSaleLine(33.33, 3, null, 10);
    expect(line.net).toBe(89.99);
    expect(line.taxable).toBeNull();
    const totals = saleTotals([line, priceSaleLine(12.5, 10, 12, 0)]);
    expect(totals.gstKnown).toBe(false);
    expect(totals.cgst).toBeNull();
    expect(totals.net).toBe(215);
    expect(totals.roundOff).toBe(0.01);
  });

  it('totals a bill with the round-off to the rupee', () => {
    // Two batches of the same medicine with different MRPs are two invoice lines.
    const totals = saleTotals([
      priceSaleLine(2.35, 10, 12, 5),
      priceSaleLine(2.6, 5, 12, 5),
      priceSaleLine(145, 1, 5, 5),
    ]);
    expect(totals.gross).toBe(181.5);
    expect(totals.discount).toBe(9.08);
    expect(totals.taxable).toBe(162.15);
    expect(totals.cgst).toBe(5.14);
    expect(totals.sgst).toBe(5.13);
    expect(totals.roundOff).toBe(-0.42);
    expect(totals.net).toBe(172);
    expect(totals.gstKnown).toBe(true);
  });

  it('charges IP issues at batch MRP', () => {
    expect(ipIssueAmount(3, 1.005)).toBe(3.02);
    expect(ipIssueAmount(10, 12.5)).toBe(125);
  });

  it('refunds partial returns proportionally and the last return exactly', () => {
    expect(refundFor(100, 3, 0, 0, 1)).toBe(33.33);
    expect(refundFor(100, 3, 1, 33.33, 1)).toBe(33.33);
    expect(refundFor(100, 3, 2, 66.66, 1)).toBe(33.34);
    expect(refundFor(100, 3, 0, 0, 3)).toBe(100);
    expect(refundFor(100, 3, 0, 0, 0)).toBe(0);
  });
});

describe('money paid back, as the server pays it', () => {
  const at = '2026-11-07T05:00:00Z';
  const card: PharmacyPaymentDto = {
    id: 'p-card',
    mode: 'Card',
    amount: 600,
    reference: '627910123456',
    receivedAt: at,
    isRefund: false,
  };
  const cash: PharmacyPaymentDto = {
    id: 'p-cash',
    mode: 'Cash',
    amount: 400,
    reference: null,
    receivedAt: at,
    isRefund: false,
  };
  const due: PharmacyPaymentDto = {
    id: 'p-due',
    mode: 'Credit',
    amount: 200,
    reference: null,
    receivedAt: at,
    isRefund: false,
  };

  it('goes back to the card a card sale was paid by, never in cash', () => {
    expect(refundSplit([card], 600)).toEqual([
      { paymentId: 'p-card', mode: 'Card', amount: 600, reference: '627910123456' },
    ]);
    expect(refundWords(refundSplit([card], 600))).toBe('₹600.00 back to the card (RRN or approval code 627910123456)');
  });

  it('shares a partial refund in proportion, to the paisa, and leaves out what is on credit', () => {
    expect(refundSplit([card, cash, due], 100).map((p) => [p.mode, p.amount])).toEqual([
      ['Cash', 40],
      ['Card', 60],
    ]);
    // 3333 paise: 1333.2 to the cash, 1999.8 to the card; the paisa left over goes to the larger remainder.
    expect(refundSplit([card, cash], 33.33).map((p) => p.amount)).toEqual([13.33, 20]);
  });

  it('pays back no more than each payment has left after earlier refunds', () => {
    const refunded: PharmacyPaymentDto = {
      id: 'r1',
      mode: 'Card',
      amount: 540,
      reference: '627910123456',
      receivedAt: at,
      isRefund: true,
      refundOfPaymentId: 'p-card',
    };
    expect(refundSplit([card, cash, refunded], 1000).map((p) => [p.mode, p.amount])).toEqual([
      ['Cash', 400],
      ['Card', 60],
    ]);
    expect(refundWords([])).toBe('Nothing is paid back');
  });
});
