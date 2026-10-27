import { DEFAULT_PRINT_TEMPLATE, type Letterhead, type PrintTemplate } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import type { BillDetail, BillLineDto, ReceiptDto } from './api';
import { billTitle, buildCounterBillHtml, buildReceiptHtml } from './print';

const facility: Letterhead = {
  name: 'Nana Hospitals',
  legalName: 'Nana Hospitals Pvt Ltd',
  address: '12 MG Road, Pune, Maharashtra, 411001',
  gstin: '27AAACN1234F1Z5',
  pan: 'AAACN1234F',
  phone: '+912012345678',
};

const stationery: PrintTemplate = {
  ...DEFAULT_PRINT_TEMPLATE,
  letterhead: 'Preprinted',
  topSpaceMm: 40,
  closingNote: 'Subject to Pune jurisdiction.',
};

function billLine(overrides: Partial<BillLineDto> = {}): BillLineDto {
  return {
    id: 'l1',
    chargeId: 'c1',
    serviceItemId: 's1',
    serviceCode: 'CONS-NEW',
    description: 'Consultation',
    category: 'Consultation',
    sacCode: '999312',
    quantity: 1,
    unitPrice: 500,
    grossAmount: 500,
    packageCoveredAmount: 0,
    tariffDiscountAmount: 0,
    lineDiscountAmount: 0,
    billDiscountShare: 0,
    taxRate: 0,
    taxableAmount: 500,
    cgstAmount: 0,
    sgstAmount: 0,
    igstAmount: 0,
    netAmount: 500,
    isDiscountAllowed: true,
    isPayerPayable: true,
    practitionerId: null,
    practitionerName: 'Dr. Asha Rao',
    departmentId: null,
    serviceDate: '2026-09-25T06:30:00Z',
    isReturn: false,
    ...overrides,
  };
}

function bill(overrides: Partial<BillDetail> = {}): BillDetail {
  return {
    id: 'b1',
    billNumber: 'NHOB260000001',
    type: 'OutPatient',
    isFinalBill: false,
    status: 'Paid',
    facilityId: 'f1',
    patientId: 'p1',
    uhid: 'NH26000123',
    patientName: 'Ravi Kumar',
    visitId: 'v1',
    admissionId: null,
    ipAccountId: null,
    payerType: 'Self',
    payerId: null,
    payerName: null,
    tariffPlanId: null,
    tariffPlanName: 'General',
    policyId: null,
    preAuthorizationId: null,
    coPayPercent: 0,
    payerCoverageLimit: null,
    billDiscountPercent: null,
    billDiscountValue: null,
    discountReason: null,
    approvedDiscountAmount: 0,
    grossAmount: 500,
    packageCoveredAmount: 0,
    tariffDiscountAmount: 0,
    lineDiscountAmount: 0,
    billDiscountAmount: 0,
    discountAmount: 0,
    taxableAmount: 500,
    cgstAmount: 0,
    sgstAmount: 0,
    igstAmount: 0,
    placeOfSupply: null,
    isInterState: false,
    taxAmount: 0,
    roundOffAmount: 0,
    netAmount: 500,
    payerAmount: 0,
    patientAmount: 500,
    coPayAmount: 0,
    nonPayableAmount: 0,
    paidAmount: 500,
    refundedAmount: 0,
    creditAmount: 0,
    dueAmount: 0,
    refundableAmount: 0,
    payerSettledAmount: 0,
    payerDisallowedAmount: 0,
    isTaxInvoice: false,
    createdAt: '2026-09-25T06:30:00Z',
    finalizedAt: '2026-09-25T06:35:00Z',
    cancelledAt: null,
    cancelReason: null,
    notes: null,
    lines: [billLine()],
    receipts: [
      {
        id: 'r1',
        receiptNumber: 'NHRC260000001',
        purpose: 'BillPayment',
        amount: 500,
        modes: 'Cash + Upi',
        receivedAt: '2026-09-25T06:36:00Z',
      },
    ],
    refunds: [],
    creditNotes: [],
    pendingDiscount: null,
    package: null,
    version: 3,
    ...overrides,
  };
}

describe('bill prints', () => {
  it('calls an exempt bill a bill of supply and a taxed one a tax invoice', () => {
    expect(billTitle(bill())).toBe('Bill of supply');
    expect(billTitle(bill({ isTaxInvoice: true }))).toBe('Tax invoice');
    expect(billTitle(bill({ type: 'InPatient', isFinalBill: true }))).toBe('Bill of supply · IP final bill');
    expect(billTitle(bill({ status: 'Draft' }))).toBe('Estimate (draft bill)');
  });

  it('prints the hospital GSTIN, SAC codes, payments and the amount in words on the 80 mm bill', () => {
    const html = buildCounterBillHtml(bill(), facility);
    expect(html).toContain('GSTIN 27AAACN1234F1Z5');
    expect(html).toContain('SAC 999312');
    expect(html).toContain('Rupees Five Hundred Only');
    expect(html).toContain('Cash + UPI');
    expect(html).toContain('size: 80mm auto');
  });

  it('shows the GST rate of taxed services and the CGST and SGST totals', () => {
    const taxed = billLine({
      taxRate: 18,
      taxableAmount: 1000,
      cgstAmount: 90,
      sgstAmount: 90,
      igstAmount: 0,
      netAmount: 1180,
      grossAmount: 1000,
      unitPrice: 1000,
    });
    const html = buildCounterBillHtml(
      bill({ isTaxInvoice: true, lines: [taxed], cgstAmount: 90, sgstAmount: 90, taxAmount: 180 }),
      facility,
    );
    expect(html).toContain('GST 18%');
    expect(html).toContain('<td>CGST</td>');
    expect(html).toContain('Tax invoice');
  });

  it('bills a payer registered in another state IGST, with the place of supply', () => {
    const taxed = billLine({
      taxRate: 18,
      taxableAmount: 1000,
      cgstAmount: 0,
      sgstAmount: 0,
      igstAmount: 180,
      netAmount: 1180,
    });
    const html = buildCounterBillHtml(
      bill({
        isTaxInvoice: true,
        lines: [taxed],
        cgstAmount: 0,
        sgstAmount: 0,
        igstAmount: 180,
        taxAmount: 180,
        placeOfSupply: '32',
        isInterState: true,
      }),
      facility,
    );
    expect(html).toContain('<td>IGST</td>');
    expect(html).not.toContain('<td>CGST</td>');
    expect(html).toContain('<td>Place of supply</td><td class="right">Kerala (32)</td>');
  });

  it('marks returns and cancelled bills', () => {
    const html = buildCounterBillHtml(
      bill({
        status: 'Cancelled',
        cancelReason: 'Wrong patient',
        cancelledAt: '2026-09-25T07:00:00Z',
        lines: [billLine({ isReturn: true, grossAmount: -100, netAmount: -100 })],
      }),
      facility,
    );
    expect(html).toContain('(return)');
    expect(html).toContain('CANCELLED');
  });

  it('escapes everything that comes from records', () => {
    const html = buildCounterBillHtml(bill({ patientName: '<script>alert(1)</script>' }), facility);
    expect(html).not.toContain('<script>alert');
    expect(html).toContain('&lt;script&gt;');
  });
});

describe('receipt prints', () => {
  const receipt: ReceiptDto = {
    id: 'r1',
    receiptNumber: 'NHRC260000001',
    purpose: 'BillPayment',
    patientId: 'p1',
    uhid: 'NH26000123',
    patientName: 'Ravi Kumar',
    billId: 'b1',
    billNumber: 'NHOB260000001',
    ipAccountId: null,
    amount: 1250.5,
    source: 'Counter',
    counter: 'OPD Counter 1',
    receivedByName: 'Meena',
    receivedAt: '2026-09-25T06:36:00Z',
    notes: null,
    payments: [
      {
        mode: 'Card',
        amount: 1000,
        reference: null,
        cardLast4: '4242',
        cardNetwork: 'Visa',
        chequeNumber: null,
        chequeBank: null,
        chequeDate: null,
      },
      {
        mode: 'Cash',
        amount: 250.5,
        reference: null,
        cardLast4: null,
        cardNetwork: null,
        chequeNumber: null,
        chequeBank: null,
        chequeDate: null,
      },
    ],
  };

  it('prints each mode with its evidence and the total in words', () => {
    const html = buildReceiptHtml(receipt, facility, 'Receipt80');
    expect(html).toContain('Visa ending 4242');
    expect(html).toContain('Rupees One Thousand Two Hundred Fifty and Fifty Paise Only');
    expect(html).toContain('NHOB260000001');
    expect(html).toContain('size: 80mm auto');
  });

  it('on pre-printed stationery keeps the tax ids and the title, and leaves room for the letterhead', () => {
    const html = buildReceiptHtml(receipt, { ...facility, templates: { receipt: stationery } }, 'A4');

    expect(html).toContain('@page { size: A4; margin: 52mm 12mm 12mm; }');
    expect(html).toContain('GSTIN 27AAACN1234F1Z5 · PAN AAACN1234F');
    expect(html).toContain('<div class="h-title">Payment receipt</div>');
    expect(html).not.toContain('Nana Hospitals Pvt Ltd');
    expect(html).not.toContain('12 MG Road');
    expect(html).toContain('<div class="closing-note">Subject to Pune jurisdiction.</div>');
  });

  it('prints the letterhead on the 80 mm roll, which is never pre-printed, and the bills template only for bills', () => {
    const html = buildCounterBillHtml(bill(), { ...facility, templates: { bill: stationery } });

    expect(html).toContain('<header class="lh compact">');
    expect(html).toContain('Nana Hospitals Pvt Ltd');
    expect(html).toContain('margin: 2mm;');
    expect(html).toContain('<div class="closing-note">Subject to Pune jurisdiction.</div>');
    expect(buildReceiptHtml(receipt, { ...facility, templates: { bill: stationery } }, 'A4')).not.toContain(
      'closing-note">',
    );
  });

  it('titles IP advances as advance receipts', () => {
    expect(buildReceiptHtml({ ...receipt, purpose: 'Deposit', billNumber: null }, facility, 'A4')).toContain(
      'Advance receipt (in-patient)',
    );
  });
});
