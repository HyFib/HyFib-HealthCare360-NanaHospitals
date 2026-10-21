import { describe, expect, it } from 'vitest';
import type { ShareRuleDto } from './api';
import {
  checkPayoutForm,
  checkRuleForm,
  feeRate,
  feeTotal,
  isPeriod,
  monthStart,
  needsPaymentReference,
  previousMonth,
  ruleScope,
  ruleShare,
  type PayoutForm,
  type RuleForm,
} from './logic';

function rule(overrides: Partial<ShareRuleDto> = {}): ShareRuleDto {
  return {
    id: 'r1',
    practitionerId: 'd1',
    practitionerName: 'Dr. Asha Rao',
    serviceItemId: null,
    serviceCode: null,
    category: null,
    payerType: null,
    method: 'Percentage',
    value: 40,
    effectiveFrom: '2026-10-01',
    effectiveTo: null,
    isActive: true,
    notes: null,
    version: 3,
    ...overrides,
  };
}

const payout = (overrides: Partial<PayoutForm> = {}): PayoutForm => ({
  doctorId: 'd1',
  from: '2026-09-01',
  to: '2026-09-30',
  tds: '',
  deductions: [],
  notes: '',
  ...overrides,
});

const ruleForm = (overrides: Partial<RuleForm> = {}): RuleForm => ({
  doctorId: 'd1',
  scope: 'all',
  serviceId: null,
  category: '',
  payerType: '',
  method: 'Percentage',
  value: '40',
  from: '2026-10-01',
  to: '',
  active: true,
  notes: '',
  version: null,
  ...overrides,
});

describe('payout and statement periods', () => {
  it('prepares a payout for the whole of the previous calendar month', () => {
    expect(previousMonth('2026-10-20')).toEqual({ from: '2026-09-01', to: '2026-09-30' });
    expect(previousMonth('2026-10-01')).toEqual({ from: '2026-09-01', to: '2026-09-30' });
    expect(previousMonth('2026-08-31')).toEqual({ from: '2026-07-01', to: '2026-07-31' });
  });

  it('goes back into the previous year in January, and knows February’s length', () => {
    expect(previousMonth('2027-01-15')).toEqual({ from: '2026-12-01', to: '2026-12-31' });
    expect(previousMonth('2028-03-05')).toEqual({ from: '2028-02-01', to: '2028-02-29' });
    expect(previousMonth('2027-03-05')).toEqual({ from: '2027-02-01', to: '2027-02-28' });
  });

  it('starts a statement on the first of the month', () => {
    expect(monthStart('2026-10-20')).toBe('2026-10-01');
    expect(monthStart('2026-10-01')).toBe('2026-10-01');
  });

  it('takes a period that ends on or after its start, with both days chosen', () => {
    expect(isPeriod('2026-10-01', '2026-10-31')).toBe(true);
    expect(isPeriod('2026-10-20', '2026-10-20')).toBe(true);
    expect(isPeriod('2026-10-21', '2026-10-20')).toBe(false);
    expect(isPeriod('', '2026-10-20')).toBe(false);
    expect(isPeriod('2026-10-01', '')).toBe(false);
  });
});

describe('how rules and fees read', () => {
  it('names what a rule applies to: one service, a category or every service', () => {
    expect(ruleScope(rule({ serviceItemId: 's1', serviceCode: 'PROC-DRESS', category: 'Procedure' }))).toBe(
      'Service PROC-DRESS',
    );
    expect(ruleScope(rule({ category: 'Procedure' }))).toBe('Procedure');
    expect(ruleScope(rule())).toBe('Every service');
  });

  it('adds the payer type a rule is limited to', () => {
    expect(ruleScope(rule({ category: 'Consultation', payerType: 'GovernmentScheme' }))).toBe(
      'Consultation · Government scheme patients',
    );
    expect(ruleScope(rule({ payerType: 'Self' }))).toBe('Every service · Self pay patients');
  });

  it('shows a share as a percentage or as rupees per unit', () => {
    expect(ruleShare('Percentage', 40)).toBe('40%');
    expect(ruleShare('Percentage', 12.5)).toBe('12.5%');
    expect(ruleShare('FixedPerUnit', 250)).toBe('₹250.00 per unit');
    expect(feeRate('Percentage', 80)).toBe('80%');
    expect(feeRate('FixedPerUnit', 1500)).toBe('₹1,500.00/unit');
  });

  it('totals fee lines to the paisa, a reversal taking its fee back', () => {
    expect(feeTotal([])).toBe(0);
    expect(feeTotal([{ shareAmount: 0.1 }, { shareAmount: 0.2 }])).toBe(0.3);
    expect(feeTotal([{ shareAmount: 800 }, { shareAmount: 300 }, { shareAmount: -800 }, { shareAmount: 150.55 }])).toBe(
      450.55,
    );
  });
});

describe('payout form (mirrors CreatePayoutRequestValidator)', () => {
  it('sends the doctor and the period, with the hospital’s TDS and no deductions when none are entered', () => {
    expect(checkPayoutForm(payout())).toEqual({
      periodOk: true,
      tdsError: null,
      deductionProblem: false,
      request: {
        practitionerId: 'd1',
        periodFrom: '2026-09-01',
        periodTo: '2026-09-30',
        deductions: null,
        tdsPercent: null,
        notes: null,
      },
    });
  });

  it('sends a TDS percentage of 0 to 100 with at most two decimals', () => {
    expect(checkPayoutForm(payout({ tds: ' 7.5 % ' })).request?.tdsPercent).toBe(7.5);
    expect(checkPayoutForm(payout({ tds: '0' })).request?.tdsPercent).toBe(0);

    for (const tds of ['101', 'ten', '10.125']) {
      const check = checkPayoutForm(payout({ tds }));
      expect(check.tdsError).not.toBeNull();
      expect(check.request).toBeNull();
    }
    expect(checkPayoutForm(payout({ tds: '101' })).tdsError).toBe('Enter a percentage between 0 and 100.');
  });

  it('sends each deduction described and in rupees, the typed amount read as the counter reads it', () => {
    const check = checkPayoutForm(
      payout({
        deductions: [
          { key: 1, description: '  Advance paid on 5 Sep ', amount: '₹1,250.50' },
          { key: 2, description: 'Consumables recovery', amount: '200' },
        ],
        notes: '  September fees ',
      }),
    );

    expect(check.deductionProblem).toBe(false);
    expect(check.request?.deductions).toEqual([
      { description: 'Advance paid on 5 Sep', amount: 1250.5 },
      { description: 'Consumables recovery', amount: 200 },
    ]);
    expect(check.request?.notes).toBe('September fees');
  });

  it('holds the payout back while a deduction has no description or no valid amount', () => {
    for (const deduction of [
      { key: 1, description: ' ', amount: '200' },
      { key: 1, description: 'Advance', amount: '' },
      { key: 1, description: 'Advance', amount: '0' },
      { key: 1, description: 'Advance', amount: '-200' },
      { key: 1, description: 'Advance', amount: '20.005' },
    ]) {
      const check = checkPayoutForm(payout({ deductions: [deduction] }));
      expect(check.deductionProblem).toBe(true);
      expect(check.request).toBeNull();
    }
  });

  it('needs a doctor and a period that ends on or after its start', () => {
    expect(checkPayoutForm(payout({ doctorId: null })).request).toBeNull();

    const backwards = checkPayoutForm(payout({ from: '2026-10-01', to: '2026-09-30' }));
    expect(backwards.periodOk).toBe(false);
    expect(backwards.request).toBeNull();

    expect(checkPayoutForm(payout({ to: '' })).periodOk).toBe(false);
  });
});

describe('paying a payout (mirrors DoctorPayout.MarkPaid)', () => {
  it('needs the reference of a bank transfer, UPI payment or cheque, but not of cash', () => {
    expect(needsPaymentReference('Cash', '')).toBe(false);
    expect(needsPaymentReference('NetBanking', '')).toBe(true);
    expect(needsPaymentReference('Upi', '   ')).toBe(true);
    expect(needsPaymentReference('Cheque', '004512')).toBe(false);
    expect(needsPaymentReference('NetBanking', 'UTR123456789')).toBe(false);
  });
});

describe('sharing-rule form (mirrors SaveShareRuleRequestValidator)', () => {
  it('sends a percentage rule for every service, from its start date with no end', () => {
    expect(checkRuleForm(ruleForm())).toEqual({
      valueError: null,
      datesOk: true,
      request: {
        practitionerId: 'd1',
        serviceItemId: null,
        category: null,
        payerType: null,
        method: 'Percentage',
        value: 40,
        effectiveFrom: '2026-10-01',
        effectiveTo: null,
        isActive: true,
        notes: null,
        version: null,
      },
    });
  });

  it('sends only what the scope names', () => {
    const both = { serviceId: 's1', category: 'Procedure' } as const;

    expect(checkRuleForm(ruleForm({ ...both, scope: 'service' })).request).toMatchObject({
      serviceItemId: 's1',
      category: null,
    });
    expect(checkRuleForm(ruleForm({ ...both, scope: 'category' })).request).toMatchObject({
      serviceItemId: null,
      category: 'Procedure',
    });
    expect(checkRuleForm(ruleForm({ ...both, scope: 'all' })).request).toMatchObject({
      serviceItemId: null,
      category: null,
    });
  });

  it('needs the service or the category its scope names', () => {
    expect(checkRuleForm(ruleForm({ scope: 'service' })).request).toBeNull();
    expect(checkRuleForm(ruleForm({ scope: 'category' })).request).toBeNull();
  });

  it('takes a percentage of 0 to 100, or rupees per unit, and says why not', () => {
    expect(checkRuleForm(ruleForm({ value: '0' })).request?.value).toBe(0);
    expect(checkRuleForm(ruleForm({ method: 'FixedPerUnit', value: '₹1,500' })).request).toMatchObject({
      method: 'FixedPerUnit',
      value: 1500,
    });

    const tooMuch = checkRuleForm(ruleForm({ value: '120' }));
    expect(tooMuch.valueError).toBe('Enter a percentage between 0 and 100.');
    expect(tooMuch.request).toBeNull();
    expect(checkRuleForm(ruleForm({ method: 'FixedPerUnit', value: '-5' })).valueError).toBe(
      'The amount cannot be negative.',
    );
  });

  it('says nothing about an empty share, but sends no rule without one', () => {
    const empty = checkRuleForm(ruleForm({ value: '' }));
    expect(empty.valueError).toBeNull();
    expect(empty.request).toBeNull();
  });

  it('needs a start date, and an end on or after it', () => {
    expect(checkRuleForm(ruleForm({ to: '2026-12-31' })).request?.effectiveTo).toBe('2026-12-31');
    expect(checkRuleForm(ruleForm({ to: '2026-10-01' })).datesOk).toBe(true);

    const backwards = checkRuleForm(ruleForm({ to: '2026-09-30' }));
    expect(backwards.datesOk).toBe(false);
    expect(backwards.request).toBeNull();
    expect(checkRuleForm(ruleForm({ from: '' })).request).toBeNull();
  });

  it('sends the hospital-wide default without a doctor, the payer type, notes, and an edited rule’s version', () => {
    expect(
      checkRuleForm(
        ruleForm({ doctorId: null, payerType: 'GovernmentScheme', notes: '  CGHS rate  ', active: false, version: 7 }),
      ).request,
    ).toMatchObject({
      practitionerId: null,
      payerType: 'GovernmentScheme',
      notes: 'CGHS rate',
      isActive: false,
      version: 7,
    });
  });
});
