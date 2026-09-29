import { describe, expect, it } from 'vitest';
import type { Letterhead } from '@/modules/pharmacy/print';
import type { CanteenSaleDto, DailySalesSummary } from './api';
import { buildCanteenReceiptHtml, buildDailyReportHtml, canteenBillTitle } from './print';

const letterhead: Letterhead = { name: 'Nana Hospitals Pvt Ltd', address: '12 MG Road, Pune, Maharashtra, 411001', phone: null, gstin: '27AAACN1234F1Z5' };

const sale: CanteenSaleDto = {
  id: 's1',
  billNumber: 'NHCN260000123',
  soldAt: '2026-09-29T04:30:00Z',
  businessDate: '2026-09-29',
  customerType: 'Visitor',
  customerName: 'Asha Rao',
  employeeCode: null,
  employeeName: null,
  taxableAmount: 36,
  cgstAmount: 2.01,
  sgstAmount: 1.99,
  grossAmount: 40,
  roundOff: 0,
  netAmount: 40,
  paymentMode: 'Upi',
  paymentReference: 'UTR99',
  status: 'Completed',
  cashierName: 'Ravi',
  cancelledAt: null,
  cancelReason: null,
  lines: [
    { menuItemId: 'm1', itemName: 'Tea', hsnSac: '996333', quantity: 2, unitPrice: 10, gstRate: 5, taxableAmount: 19.05, cgstAmount: 0.48, sgstAmount: 0.47, lineTotal: 20 },
    { menuItemId: 'm2', itemName: 'Water 1 L', hsnSac: '220190', quantity: 1, unitPrice: 20, gstRate: 18, taxableAmount: 16.95, cgstAmount: 1.53, sgstAmount: 1.52, lineTotal: 20 },
  ],
};

describe('canteen receipt', () => {
  it('is a tax invoice with the supplier details, each line’s HSN/SAC code and GST rate, and the GST by rate', () => {
    const html = buildCanteenReceiptHtml(sale, letterhead);

    expect(html).toContain('size: 80mm auto');
    expect(html).toContain('<div class="title">Tax invoice</div>');
    expect(html).not.toMatch(/bill of supply/i);
    expect(html).toContain('Nana Hospitals Pvt Ltd');
    expect(html).toContain('12 MG Road, Pune, Maharashtra, 411001');
    expect(html).toContain('<div class="small">GSTIN 27AAACN1234F1Z5</div>');
    expect(html).toContain('Bill <strong>NHCN260000123</strong>');
    expect(html).toContain('2 × 10.00 · SAC 996333 · GST 5%');
    expect(html).toContain('1 × 20.00 · HSN 220190 · GST 18%');
    expect(html).toContain('<tr><td>Taxable value</td><td class="right">36.00</td></tr>');
    expect(html).toContain('<tr><td>CGST</td><td class="right">2.01</td></tr>');
    expect(html).toContain('<tr><td>SGST</td><td class="right">1.99</td></tr>');
    expect(html).toContain('<tr class="net"><td>Total</td><td class="right">₹40.00</td></tr>');
    expect(html).toContain('<tr><td>5%</td><td>19.05</td><td>0.48</td><td>0.47</td></tr>');
    expect(html).toContain('<tr><td>18%</td><td>16.95</td><td>1.53</td><td>1.52</td></tr>');
    expect(html).toContain('Paid by UPI · UTR99');
  });

  it('leaves out the GSTIN line when the facility has no GSTIN', () => {
    for (const gstin of [null, undefined, '']) {
      const html = buildCanteenReceiptHtml(sale, { name: 'Nana Hospitals', gstin });
      expect(html).not.toContain('GSTIN');
      expect(html).toContain('Tax invoice');
      expect(html).toContain('SAC 996333');
    }
  });

  it('escapes every value from the bill and the facility master', () => {
    const html = buildCanteenReceiptHtml(
      {
        ...sale,
        customerName: 'Asha <Rao>',
        paymentReference: 'UTR"><img src=x>',
        cashierName: 'R & K',
        lines: [{ ...sale.lines[0]!, itemName: 'Tea <special>', hsnSac: '99<b>' }],
      },
      { name: 'Nana & Co <Hospitals>', address: '<MG Road>', gstin: '27AAACN1234F1Z5<script>' },
    );

    expect(html).toContain('Nana &amp; Co &lt;Hospitals&gt;');
    expect(html).toContain('&lt;MG Road&gt;');
    expect(html).toContain('GSTIN 27AAACN1234F1Z5&lt;script&gt;');
    expect(html).toContain('Tea &lt;special&gt;');
    expect(html).toContain('SAC 99&lt;b&gt;');
    expect(html).toContain('Visitor · Asha &lt;Rao&gt;');
    expect(html).toContain('UTR&quot;&gt;&lt;img src=x&gt;');
    expect(html).toContain('Cashier: R &amp; K');
    for (const raw of ['<script>', '<special>', '<Rao>', '<img', '<b>', '<Hospitals>', '<MG Road>']) {
      expect(html).not.toContain(raw);
    }
  });

  it('leaves out the code of lines billed before codes were recorded', () => {
    const html = buildCanteenReceiptHtml({ ...sale, lines: [{ ...sale.lines[0]!, hsnSac: null }] }, letterhead);

    expect(html).toContain('2 × 10.00 · GST 5%');
    expect(html).not.toContain('SAC');
  });

  it('marks duplicates and cancelled bills', () => {
    expect(buildCanteenReceiptHtml(sale, letterhead, { duplicate: true })).toContain('DUPLICATE');
    expect(buildCanteenReceiptHtml({ ...sale, status: 'Cancelled' }, letterhead)).toContain('CANCELLED');
  });

  it('is a bill of supply when nothing on it is taxed', () => {
    const coconut = { ...sale.lines[1]!, itemName: 'Tender coconut', hsnSac: '080119', gstRate: 0, cgstAmount: 0, sgstAmount: 0 };

    expect(canteenBillTitle([{ gstRate: 5 }, { gstRate: 18 }])).toBe('Tax invoice');
    expect(canteenBillTitle([{ gstRate: 0 }])).toBe('Bill of supply');
    expect(canteenBillTitle([{ gstRate: 5 }, { gstRate: 0 }])).toBe('Invoice-cum-bill of supply');
    expect(buildCanteenReceiptHtml({ ...sale, lines: [coconut] }, letterhead)).toContain('<div class="title">Bill of supply</div>');
    expect(buildCanteenReceiptHtml({ ...sale, lines: [sale.lines[0]!, coconut] }, letterhead)).toContain('HSN 080119 · GST 0%');
  });
});

describe('canteen day-end report', () => {
  const report: DailySalesSummary = {
    date: '2026-09-29',
    bills: 3,
    cancelledBills: 1,
    taxableAmount: 171.71,
    cgst: 4.16,
    sgst: 4.13,
    roundOff: 0,
    netSales: 180,
    byPaymentMode: [{ key: 'Upi', bills: 3, amount: 180 }],
    byCustomerType: [{ key: 'Visitor', bills: 3, amount: 180 }],
    byCategory: [],
    items: [{ menuItemId: 'm1', itemName: 'Tea <masala>', category: 'Beverages', quantitySold: 3, amount: 30, closingStock: null }],
    byHsnSac: [
      { hsnSac: '080119', gstRate: 0, quantity: 1, taxableAmount: 50, cgst: 0, sgst: 0, total: 50 },
      { hsnSac: '220190', gstRate: 18, quantity: 1, taxableAmount: 16.95, cgst: 1.53, sgst: 1.52, total: 20 },
      { hsnSac: '996333', gstRate: 5, quantity: 3, taxableAmount: 95.24, cgst: 2.39, sgst: 2.37, total: 100 },
      { hsnSac: null, gstRate: 5, quantity: 1, taxableAmount: 9.52, cgst: 0.24, sgst: 0.24, total: 10 },
    ],
  };

  it('prints the HSN/SAC summary by code and rate with a total row', () => {
    const html = buildDailyReportHtml(report, letterhead);

    expect(html).toContain('size: A4');
    expect(html).toContain('GSTIN 27AAACN1234F1Z5');
    expect(html).toContain('<h2>HSN/SAC summary</h2>');
    expect(html).toContain(
      '<tr><td>996333</td><td class="right">5%</td><td class="right">3</td><td class="right">95.24</td><td class="right">2.39</td><td class="right">2.37</td><td class="right">100.00</td></tr>',
    );
    expect(html).toContain('<tr><td>220190</td><td class="right">18%</td>');
    expect(html).toContain('<tr><td>Not recorded</td><td class="right">5%</td>');
    expect(html).toContain(
      '<tr class="total"><td colspan="3">Total</td><td class="right">171.71</td><td class="right">4.16</td><td class="right">4.13</td><td class="right">180.00</td></tr>',
    );
    expect(html).toContain('Tea &lt;masala&gt;');
    expect(html).not.toContain('<masala>');
  });

  it('says so when the day had no sales and leaves out a missing GSTIN', () => {
    const html = buildDailyReportHtml({ ...report, bills: 0, byHsnSac: [], items: [] }, { name: 'Nana Hospitals' });

    expect(html).toContain('<p class="muted">No sales.</p>');
    expect(html).not.toContain('GSTIN');
  });
});
