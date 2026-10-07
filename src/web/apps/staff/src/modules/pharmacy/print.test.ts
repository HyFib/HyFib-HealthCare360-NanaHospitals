import { describe, expect, it } from 'vitest';
import type { PharmacySaleDetail } from './api';
import { buildLabelsHtml, buildReceiptHtml, directionsText, dispensingLabelData, gstSummaryByRate } from './print';

const sale: PharmacySaleDetail = {
  id: 's1',
  invoiceNumber: 'NHPH260000123',
  type: 'Prescription',
  storeId: 'st1',
  storeName: 'OP Pharmacy',
  patientId: 'p1',
  patientUhid: 'NH0001234',
  customerName: 'Asha <Rao>',
  customerPhone: null,
  prescriptionNumber: 'RX-9',
  prescriberName: 'Dr. Mehta',
  prescriberRegistrationNumber: 'MMC 1234',
  soldAt: '2026-09-26T05:00:00Z',
  soldBy: 'Ravi',
  counsellingNotes: 'Take after food',
  discountPercent: 5,
  grossAmount: 181.5,
  discountAmount: 9.08,
  taxableAmount: 162.15,
  cgstAmount: 5.14,
  sgstAmount: 5.13,
  roundOff: -0.42,
  netAmount: 172,
  paidAmount: 172,
  dueAmount: 0,
  returnedAmount: 0,
  refundedAmount: 0,
  status: 'Paid',
  cancelReason: null,
  items: [
    {
      id: 'i1',
      itemId: 'm1',
      itemName: 'Amoxicillin 500',
      genericName: 'Amoxicillin',
      hsnCode: '300410',
      schedule: 'H',
      batchNumber: 'AMX1',
      expiryDate: '2027-03-31',
      quantity: 10,
      unitMrp: 2.35,
      gstRate: 12,
      grossAmount: 23.5,
      discountAmount: 1.18,
      taxableAmount: 19.93,
      cgstAmount: 1.2,
      sgstAmount: 1.19,
      netAmount: 22.32,
      returnedQuantity: 0,
      isSubstitution: true,
      prescribedDrugName: 'Mox 500',
      substitutionReason: 'Out of stock',
    },
    {
      id: 'i2',
      itemId: 'm2',
      itemName: 'Pantoprazole 40',
      genericName: 'Pantoprazole',
      hsnCode: '300490',
      schedule: 'H',
      batchNumber: 'PAN7',
      expiryDate: '2027-01-31',
      quantity: 5,
      unitMrp: 2.6,
      gstRate: 12,
      grossAmount: 13,
      discountAmount: 0.65,
      taxableAmount: 11.03,
      cgstAmount: 0.66,
      sgstAmount: 0.66,
      netAmount: 12.35,
      returnedQuantity: 0,
      isSubstitution: false,
      prescribedDrugName: null,
      substitutionReason: null,
    },
  ],
  payments: [
    { id: 'pay1', mode: 'Upi', amount: 172, reference: 'UTR99', receivedAt: '2026-09-26T05:00:00Z', isRefund: false },
  ],
  version: 1,
};

describe('pharmacy prints', () => {
  it('sums GST by rate from the server lines', () => {
    expect(gstSummaryByRate(sale.items)).toEqual([{ rate: 12, taxable: 30.96, cgst: 1.86, sgst: 1.85 }]);
  });

  it('prints an escaped GST invoice with CGST/SGST, payments and the substitution note', () => {
    const html = buildReceiptHtml(sale, { name: 'Nana Hospitals', gstin: '27AAACH9876K1Z2' });
    expect(html).toContain('size: 80mm auto');
    expect(html).toContain('NHPH260000123');
    expect(html).toContain('Asha &lt;Rao&gt; (NH0001234)');
    expect(html).not.toContain('<Rao>');
    expect(html).toContain('GSTIN 27AAACH9876K1Z2');
    expect(html).toContain('HSN 300410');
    expect(html).toContain('Paid by UPI (UTR99)');
    expect(html).toContain('Substituted with the prescriber');
    expect(html).toContain('Mox 500');
  });

  it('prints the invoice as the pharmacy and canteen bills template says', () => {
    const html = buildReceiptHtml(sale, {
      name: 'Nana Hospitals',
      logo: 'data:image/png;base64,iVBORw0KGgo=',
      templates: {
        'sales-invoice': {
          letterhead: 'Printed',
          topSpaceMm: 0,
          bottomSpaceMm: 0,
          showLogo: true,
          closingNote: 'Medicines once sold are taken back only with the bill.',
          paper: null,
        },
      },
    });

    expect(html).toContain('<img class="lh-logo" src="data:image/png;base64,iVBORw0KGgo=" alt="">');
    expect(html).toContain('<div class="closing-note">Medicines once sold are taken back only with the bill.</div>');
  });

  it('marks cancelled invoices', () => {
    expect(
      buildReceiptHtml({ ...sale, status: 'Cancelled', cancelReason: 'Wrong patient' }, { name: 'Nana Hospitals' }),
    ).toContain('CANCELLED: Wrong patient');
  });

  it('prints one 50 × 25 mm label per medicine', () => {
    const html = buildLabelsHtml(
      [
        {
          patientName: 'Asha Rao',
          drugName: 'Amoxicillin 500',
          quantity: 10,
          unit: 'TAB',
          directions: '1 tab · thrice a day',
          batchNumber: 'AMX1',
          expiryDate: '2027-03-31',
          dispensedOn: '2026-09-26',
        },
        { patientName: 'Asha Rao', drugName: 'Pantoprazole 40', quantity: 5, dispensedOn: '2026-09-26' },
      ],
      'Nana Hospitals',
    );
    expect(html).toContain('size: 50mm 25mm');
    expect(html.match(/class="label"/g)).toHaveLength(2);
    expect(html).toContain('Amoxicillin 500 × 10 TAB');
    expect(html).toContain('Exp Mar 2027');
  });

  it('gives a label printer the same facts as the label page', () => {
    expect(
      dispensingLabelData(
        {
          patientName: 'Asha Rao',
          drugName: 'Amoxicillin 500',
          quantity: 10,
          unit: 'TAB',
          directions: '1 tab · thrice a day',
          batchNumber: 'AMX1, AMX2',
          expiryDate: '2027-03-31',
          dispensedOn: '2026-09-26',
        },
        'Nana Hospitals',
      ),
    ).toEqual({
      patientName: 'Asha Rao',
      medicine: 'Amoxicillin 500',
      quantity: '10 TAB',
      directions: '1 tab · thrice a day',
      batch: 'AMX1, AMX2',
      expiry: '2027-03-31',
      dispensedOn: '2026-09-26',
      hospital: 'Nana Hospitals',
    });
    expect(
      dispensingLabelData({ patientName: 'Asha Rao', drugName: 'ORS', quantity: 2, dispensedOn: '2026-09-26' }, ''),
    ).toMatchObject({ directions: null, batch: null, expiry: null, hospital: null, quantity: '2' });
  });

  it('writes directions in plain words', () => {
    expect(
      directionsText({ dose: '1 tab', frequency: 'Twice a day', durationDays: 5, instructions: ' after food ' }),
    ).toBe('1 tab · Twice a day · 5 days · after food');
    expect(directionsText({ dose: '5 ml', durationDays: 1 })).toBe('5 ml · 1 day');
  });
});
