import { describe, expect, it } from 'vitest';
import type { BillDetail, BillLineDto, ReceiptDto } from './api';
import { billTitle, buildBillHtml, buildReceiptHtml, gstSummary } from './print';

const facility = {
  name: 'Nana Hospitals',
  legalName: 'Nana Hospitals Pvt Ltd',
  address: { line1: '12 MG Road', city: 'Pune', state: 'Maharashtra', pinCode: '411001' },
  gstin: '27AAACN1234F1Z5',
  phone: '+912012345678',
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
    receipts: [{ id: 'r1', receiptNumber: 'NHRC260000001', purpose: 'BillPayment', amount: 500, modes: 'Cash + Upi', receivedAt: '2026-09-25T06:36:00Z' }],
    refunds: [],
    creditNotes: [],
    pendingDiscount: null,
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

  it('prints the hospital GSTIN, SAC codes and the amount in words', () => {
    const html = buildBillHtml(bill(), facility, 'A4');
    expect(html).toContain('GSTIN 27AAACN1234F1Z5');
    expect(html).toContain('999312');
    expect(html).toContain('Rupees Five Hundred Only');
    expect(html).toContain('Cash + UPI');
    expect(html).not.toContain('CGST');
  });

  it('shows CGST and SGST columns and a GST summary on a tax invoice', () => {
    const taxed = billLine({ taxRate: 18, taxableAmount: 1000, cgstAmount: 90, sgstAmount: 90, netAmount: 1180, grossAmount: 1000, unitPrice: 1000 });
    const html = buildBillHtml(bill({ isTaxInvoice: true, lines: [taxed], cgstAmount: 90, sgstAmount: 90, taxAmount: 180 }), facility, 'A4');
    expect(html).toContain('<th class="num">CGST</th>');
    expect(html).toContain('GST summary');
    expect(html).toContain('9%');
  });

  it('marks returns and cancelled bills', () => {
    const html = buildBillHtml(
      bill({ status: 'Cancelled', cancelReason: 'Wrong patient', cancelledAt: '2026-09-25T07:00:00Z', lines: [billLine({ isReturn: true, grossAmount: -100, netAmount: -100 })] }),
      facility,
      'Receipt80',
    );
    expect(html).toContain('(return)');
    expect(html).toContain('CANCELLED');
  });

  it('escapes everything that comes from records', () => {
    const html = buildBillHtml(bill({ patientName: '<script>alert(1)</script>' }), facility, 'A4');
    expect(html).not.toContain('<script>alert');
    expect(html).toContain('&lt;script&gt;');
  });

  it('groups GST by SAC and rate', () => {
    const rows = gstSummary([
      billLine({ id: 'a', sacCode: '999312', taxRate: 18, taxableAmount: 100, cgstAmount: 9, sgstAmount: 9 }),
      billLine({ id: 'b', sacCode: '999312', taxRate: 18, taxableAmount: 50.5, cgstAmount: 4.55, sgstAmount: 4.54 }),
      billLine({ id: 'c', sacCode: '999311', taxRate: 0 }),
    ]);
    expect(rows).toEqual([{ sac: '999312', rate: 18, taxable: 150.5, cgst: 13.55, sgst: 13.54 }]);
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
      { mode: 'Card', amount: 1000, reference: null, cardLast4: '4242', cardNetwork: 'Visa', chequeNumber: null, chequeBank: null, chequeDate: null },
      { mode: 'Cash', amount: 250.5, reference: null, cardLast4: null, cardNetwork: null, chequeNumber: null, chequeBank: null, chequeDate: null },
    ],
  };

  it('prints each mode with its evidence and the total in words', () => {
    const html = buildReceiptHtml(receipt, facility, 'Receipt80');
    expect(html).toContain('Visa ending 4242');
    expect(html).toContain('Rupees One Thousand Two Hundred Fifty and Fifty Paise Only');
    expect(html).toContain('NHOB260000001');
    expect(html).toContain('size: 80mm auto');
  });

  it('titles IP advances as advance receipts', () => {
    expect(buildReceiptHtml({ ...receipt, purpose: 'Deposit', billNumber: null }, facility, 'A4')).toContain('Advance receipt (in-patient)');
  });
});
