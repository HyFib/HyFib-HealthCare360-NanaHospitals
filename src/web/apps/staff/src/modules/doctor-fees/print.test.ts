import type { Letterhead } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import type { AccrualDto, DoctorStatement, PayoutDto } from './api';
import { buildPayoutAdviceHtml, buildStatementHtml } from './print';

const facility: Letterhead = {
  name: 'Nana Hospitals',
  legalName: 'Nana Hospitals Pvt Ltd',
  address: '12 MG Road, Pune, Maharashtra, 411001',
  gstin: '27AAACN1234F1Z5',
  pan: 'AAACN1234F',
};

function fee(overrides: Partial<AccrualDto> = {}): AccrualDto {
  return {
    id: 'a1',
    practitionerId: 'd1',
    practitionerName: 'Dr. Arjun Mehta',
    billId: 'b1',
    billNumber: 'NHOB260000101',
    serviceCode: 'PROC-DEBR',
    description: 'Wound debridement',
    category: 'Procedure',
    payerType: 'Self',
    serviceDate: '2026-10-20',
    accruedOn: '2026-10-20',
    basisAmount: 2000,
    method: 'Percentage',
    rate: 40,
    shareAmount: 800,
    status: 'Paid',
    payoutId: 'p1',
    isReversal: false,
    ...overrides,
  };
}

function payout(overrides: Partial<PayoutDto> = {}): PayoutDto {
  return {
    id: 'p1',
    payoutNumber: 'NHDP2600012',
    practitionerId: 'd1',
    practitionerName: 'Dr. Arjun Mehta',
    periodFrom: '2026-10-01',
    periodTo: '2026-10-20',
    grossAmount: 1900,
    deductionAmount: 200,
    tdsPercent: 10,
    tdsAmount: 190,
    netAmount: 1510,
    status: 'Paid',
    approvedBy: 'u2',
    approvedAt: '2026-10-20T05:30:00Z',
    paidAt: '2026-10-20T06:30:00Z',
    paidReference: 'UTR123456789012',
    paymentMode: 'NetBanking',
    notes: null,
    accrualCount: 3,
    deductions: [{ id: 'x1', description: 'Advance paid on the 1st', amount: 200 }],
    version: 5,
    ...overrides,
  };
}

const statement: DoctorStatement = {
  practitionerId: 'd1',
  practitionerName: 'Dr. Arjun <Mehta>',
  from: '2026-10-01',
  to: '2026-10-20',
  accrued: 2850,
  reversed: 800,
  inPayout: 150,
  paidOut: 1900,
  unpaid: 0,
  accruals: [
    fee(),
    fee({
      id: 'a2',
      serviceCode: 'PROC-SUTR',
      description: 'Suturing',
      method: 'FixedPerUnit',
      rate: 150,
      shareAmount: 300,
    }),
    fee({
      id: 'a3',
      description: 'Reversal: Wound debridement',
      basisAmount: -2000,
      shareAmount: -800,
      status: 'InPayout',
      payoutId: 'p2',
      isReversal: true,
    }),
  ],
  payouts: [payout()],
};

describe('doctor fee statement print', () => {
  it('names the doctor and the period, and gives the totals', () => {
    const html = buildStatementHtml(statement, facility, '2026-10-20T07:00:00Z');

    expect(html).toContain('Doctor fee statement');
    expect(html).toContain('<b>Dr. Arjun &lt;Mehta&gt;</b> · 01 Oct 2026 to 20 Oct 2026');
    expect(html).toContain('<tr><td>Fees earned</td><td class="num">₹2,850.00</td></tr>');
    expect(html).toContain('<tr><td>Reversed (cancelled bills)</td><td class="num">₹800.00</td></tr>');
    expect(html).toContain('<tr><td>In a payout</td><td class="num">₹150.00</td></tr>');
    expect(html).toContain('<tr><td>Paid out</td><td class="num">₹1,900.00</td></tr>');
    expect(html).toContain('<tr class="strong"><td>Unpaid</td><td class="num">₹0.00</td></tr>');
    expect(html).toContain('TDS is deducted under section 194J.');
  });

  it('lists each fee line with its bill, billed value, rate and share; a reversal is marked and negative', () => {
    const html = buildStatementHtml(statement, facility, '2026-10-20T07:00:00Z');

    expect(html).toContain('<td>NHOB260000101</td>');
    expect(html).toContain('<div class="muted">PROC-DEBR · Procedure</div>');
    expect(html).toContain('<td class="num">40%</td>');
    expect(html).toContain('<td class="num">₹150.00</td>');
    expect(html).toContain('<td class="num">₹300.00</td>');
    expect(html).toContain('Reversal: Wound debridement (reversal)');
    expect(html).toContain('<td class="num">-₹2,000.00</td>');
    expect(html).toContain('<td class="num">-₹800.00</td>');
    expect(html).toContain('<td>In a payout</td>');
    expect(html).toContain('<td>Paid</td>');
  });

  it('lists the payouts with their deductions, TDS and net', () => {
    const html = buildStatementHtml(statement, facility, '2026-10-20T07:00:00Z');

    expect(html).toContain('<h3>Payouts</h3>');
    expect(html).toContain('<td>NHDP2600012</td>');
    expect(html).toMatch(
      /<td class="num">₹1,900\.00<\/td><td class="num">₹200\.00<\/td><td class="num">₹190\.00<\/td><td class="num">₹1,510\.00<\/td><td>Paid<\/td>/,
    );
  });

  it('says when the period has no fees, and leaves the payouts out when there are none', () => {
    const html = buildStatementHtml(
      { ...statement, accrued: 0, reversed: 0, inPayout: 0, paidOut: 0, unpaid: 0, accruals: [], payouts: [] },
      facility,
      '2026-10-20T07:00:00Z',
    );

    expect(html).toContain('<tr><td colspan="7">No fees in this period.</td></tr>');
    expect(html).not.toContain('<h3>Payouts</h3>');
  });
});

describe('doctor payout advice print', () => {
  it('takes the deductions and TDS off the fees and gives the net in words', () => {
    const html = buildPayoutAdviceHtml(payout({ notes: 'Fees for <October>' }), facility);

    expect(html).toContain('Doctor payout advice');
    expect(html).toContain('Payout <b>NHDP2600012</b> · Paid');
    expect(html).toContain('(3 fee lines)');
    expect(html).toContain('<tr><td>Fees for the period</td><td class="num">₹1,900.00</td></tr>');
    expect(html).toContain('<tr><td>Less: Advance paid on the 1st</td><td class="num">-₹200.00</td></tr>');
    expect(html).toContain('<tr><td>Less: TDS 10% (section 194J)</td><td class="num">-₹190.00</td></tr>');
    expect(html).toContain('<tr class="strong"><td>Net paid</td><td class="num">₹1,510.00</td></tr>');
    expect(html).toContain('Rupees One Thousand Five Hundred Ten Only');
    expect(html).toContain('by Bank transfer, reference UTR123456789012');
    expect(html).toContain('Fees for &lt;October&gt;');
    expect(html).not.toContain('<October>');
  });

  it('shows no payment line before the payout is paid', () => {
    const html = buildPayoutAdviceHtml(
      payout({
        status: 'Approved',
        paidAt: null,
        paidReference: null,
        paymentMode: null,
        deductions: [],
        deductionAmount: 0,
      }),
      facility,
    );

    expect(html).toContain('Payout <b>NHDP2600012</b> · Approved, to pay');
    expect(html).not.toContain('Paid on');
    expect(html).not.toContain('Less: Advance');
  });
});
