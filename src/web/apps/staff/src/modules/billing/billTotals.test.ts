import { describe, expect, it } from 'vitest';
import {
  allowedWithoutApproval,
  calculateBillTotals,
  gstBreakdown,
  inferTariffPercent,
  NO_BILL_DISCOUNT,
  NO_DISCOUNT_AUTHORITY,
  previewDiscount,
  requiresApproval,
  SELF_PAY,
  type BillDiscountInput,
  type BillLineInput,
  type DiscountAuthority,
  type PreviewBill,
  type PreviewLine,
} from './billTotals';

/** Same defaults as TestData.Line in tests/Modules/HyFib.HealthCare360.Modules.Billing.Tests. */
function line(
  gross: number,
  options: {
    discountAllowed?: boolean;
    taxRate?: number;
    payable?: boolean;
    lineDiscount?: number;
    covered?: number;
    tariffPercent?: number;
  } = {},
): BillLineInput {
  return {
    gross,
    packageCovered: options.covered ?? 0,
    tariffDiscountPercent: options.tariffPercent ?? 0,
    lineDiscount: options.lineDiscount ?? 0,
    discountAllowed: options.discountAllowed ?? true,
    taxRate: options.taxRate ?? 0,
    payerPayable: options.payable ?? true,
  };
}

const calculate = (discount: BillDiscountInput, ...lines: BillLineInput[]) =>
  calculateBillTotals(lines, discount, SELF_PAY);
const percent = (value: number): BillDiscountInput => ({ percent: value, amount: null });
const amount = (value: number): BillDiscountInput => ({ percent: null, amount: value });

describe('BillTotalsCalculator (mirrors BillTotalsCalculatorTests)', () => {
  it.each([
    [100.4, 50.35, 151, 0.25],
    [100.4, 50.09, 150, -0.49],
    [100.25, 50.25, 151, 0.5],
  ])(
    'rounds the patient amount %s + %s to the nearest rupee half away from zero',
    (first, second, expectedNet, expectedRoundOff) => {
      const totals = calculate(NO_BILL_DISCOUNT, line(first), line(second));
      expect(totals.patientAmount).toBe(expectedNet);
      expect(totals.net).toBe(expectedNet);
      expect(totals.roundOff).toBe(expectedRoundOff);
    },
  );

  it('never lets a line discount exceed the line value', () => {
    const totals = calculate(NO_BILL_DISCOUNT, line(100, { lineDiscount: 150 }));
    expect(totals.lineDiscount).toBe(100);
    expect(totals.net).toBe(0);
  });

  it('spreads a bill percentage discount in proportion to line values', () => {
    const totals = calculate(percent(10), line(300), line(100));
    expect(totals.billDiscount).toBe(40);
    expect(totals.lines[0]!.billDiscountShare).toBe(30);
    expect(totals.lines[1]!.billDiscountShare).toBe(10);
    expect(totals.net).toBe(360);
  });

  it('gives the rounding remainder of a bill discount to the largest line so the total is exact', () => {
    const totals = calculate(amount(100), line(100), line(100), line(100));
    expect(totals.lines.map((l) => l.billDiscountShare)).toEqual([33.34, 33.33, 33.33]);
    expect(totals.billDiscount).toBe(100);
    expect(totals.net).toBe(200);
  });

  it('leaves services that cannot be discounted out of the bill discount', () => {
    const totals = calculate(percent(50), line(200), line(100, { discountAllowed: false, lineDiscount: 40 }));
    expect(totals.lines[0]!.billDiscountShare).toBe(100);
    expect(totals.lines[1]!.billDiscountShare).toBe(0);
    expect(totals.lines[1]!.lineDiscount).toBe(0);
    expect(totals.net).toBe(200);
  });

  it('caps a bill discount amount at the discountable value', () => {
    const totals = calculate(amount(1000), line(300));
    expect(totals.billDiscount).toBe(300);
    expect(totals.net).toBe(0);
  });

  it('charges GST on the discounted value and splits it into CGST and SGST', () => {
    const totals = calculate(percent(10), line(1000, { taxRate: 18 }));
    expect(totals.taxable).toBe(900);
    expect(totals.tax).toBe(162);
    expect(totals.cgst).toBe(81);
    expect(totals.sgst).toBe(81);
    expect(totals.net).toBe(1062);
  });

  it('always adds CGST and SGST back to the rounded GST', () => {
    const totals = calculate(NO_BILL_DISCOUNT, line(100.05, { taxRate: 5 }));
    expect(totals.tax).toBe(5);
    expect(totals.cgst + totals.sgst).toBe(totals.tax);

    const odd = calculate(NO_BILL_DISCOUNT, line(100.1, { taxRate: 5 }));
    expect(odd.tax).toBe(5.01);
    expect(odd.cgst).toBe(2.51);
    expect(odd.sgst).toBe(2.5);
  });

  it('applies the tariff concession to the part not covered by a package', () => {
    const totals = calculate(NO_BILL_DISCOUNT, line(1000, { covered: 600, tariffPercent: 10 }));
    expect(totals.packageCovered).toBe(600);
    expect(totals.tariffDiscount).toBe(40);
    expect(totals.net).toBe(360);
  });

  it('charges nothing for a fully package-covered line', () => {
    const totals = calculate(NO_BILL_DISCOUNT, line(1500, { covered: 1500 }), line(250));
    expect(totals.lines[0]!.net).toBe(0);
    expect(totals.net).toBe(250);
  });

  it('measures the user discount against the value after tariff concessions', () => {
    const totals = calculate(amount(90), line(1000, { tariffPercent: 10 }));
    expect(totals.tariffDiscount).toBe(100);
    expect(totals.userDiscount).toBe(90);
    expect(totals.discountBase).toBe(900);
    expect(totals.totalDiscount).toBe(190);
  });

  it('totals an empty bill to zero', () => {
    const totals = calculate(percent(10));
    expect(totals.net).toBe(0);
    expect(totals.roundOff).toBe(0);
    expect(totals.lines).toEqual([]);
  });
});

describe('Insurance split (mirrors InsuranceSplitTests)', () => {
  it('makes co-pay the patient share of payable services', () => {
    const totals = calculateBillTotals([line(10_000)], NO_BILL_DISCOUNT, {
      hasPayer: true,
      coPayPercent: 10,
      coverageLimit: null,
    });
    expect(totals.coPay).toBe(1_000);
    expect(totals.payerAmount).toBe(9_000);
    expect(totals.patientAmount).toBe(1_000);
    expect(totals.net).toBe(10_000);
  });

  it('caps the payer share at the approved amount', () => {
    const totals = calculateBillTotals([line(50_000)], NO_BILL_DISCOUNT, {
      hasPayer: true,
      coPayPercent: 0,
      coverageLimit: 40_000,
    });
    expect(totals.payerAmount).toBe(40_000);
    expect(totals.patientAmount).toBe(10_000);
  });

  it('makes the patient pay for non-payable items', () => {
    const totals = calculateBillTotals([line(10_000), line(500, { payable: false })], NO_BILL_DISCOUNT, {
      hasPayer: true,
      coPayPercent: 0,
      coverageLimit: null,
    });
    expect(totals.payerAmount).toBe(10_000);
    expect(totals.nonPayable).toBe(500);
    expect(totals.patientAmount).toBe(500);
  });

  it('rounds only the patient share', () => {
    const totals = calculateBillTotals([line(10_000.4)], NO_BILL_DISCOUNT, {
      hasPayer: true,
      coPayPercent: 5,
      coverageLimit: null,
    });
    expect(totals.coPay).toBe(500.02);
    expect(totals.payerAmount).toBe(9_500.38);
    expect(totals.patientAmount).toBe(500);
    expect(totals.roundOff).toBe(-0.02);
    expect(totals.net).toBe(10_000.38);
  });

  it('leaves nothing for the patient on a fully covered bill, even with paise', () => {
    const totals = calculateBillTotals([line(10_000.4)], NO_BILL_DISCOUNT, {
      hasPayer: true,
      coPayPercent: 0,
      coverageLimit: 20_000,
    });
    expect(totals.payerAmount).toBe(10_000.4);
    expect(totals.patientAmount).toBe(0);
    expect(totals.roundOff).toBe(0);
  });

  it('has no payer share on self-pay bills', () => {
    const totals = calculateBillTotals([line(1_000)], NO_BILL_DISCOUNT, SELF_PAY);
    expect(totals.payerAmount).toBe(0);
    expect(totals.nonPayable).toBe(0);
    expect(totals.patientAmount).toBe(1_000);
  });
});

describe('Returns and credits (mirrors ReturnsAndCreditsTests)', () => {
  it('credits a return at the sale concession and gives it its share of the bill discount', () => {
    const totals = calculateBillTotals(
      [line(1_000, { tariffPercent: 10 }), line(-200, { tariffPercent: 10 })],
      percent(10),
      SELF_PAY,
    );
    expect(totals.lines[1]!.tariffDiscount).toBe(-20);
    expect(totals.lines[1]!.billDiscountShare).toBe(-18);
    expect(totals.lines[1]!.net).toBe(-162);
    expect(totals.billDiscount).toBe(72);
    expect(totals.net).toBe(648);
  });

  it('never applies staff line discounts to a return', () => {
    const totals = calculateBillTotals([line(500), line(-100, { lineDiscount: 50 })], NO_BILL_DISCOUNT, SELF_PAY);
    expect(totals.lineDiscount).toBe(0);
    expect(totals.net).toBe(400);
  });

  it('gives the package back the part of a return it absorbed', () => {
    const totals = calculateBillTotals([line(-1_500, { covered: -500 })], NO_BILL_DISCOUNT, SELF_PAY);
    expect(totals.lines[0]!.billable).toBe(-1_000);
    expect(totals.packageCovered).toBe(-500);
    expect(totals.patientAmount).toBe(-1_000);
  });

  it('gives returns alone no discount and no negative co-pay', () => {
    const totals = calculateBillTotals([line(-150.4)], percent(10), {
      hasPayer: true,
      coPayPercent: 10,
      coverageLimit: null,
    });
    expect(totals.billDiscount).toBe(0);
    expect(totals.coPay).toBe(0);
    expect(totals.payerAmount).toBe(0);
    expect(totals.patientAmount).toBe(-150);
    expect(totals.roundOff).toBe(0.4);
  });
});

describe('DiscountPolicy (mirrors DiscountPolicyTests)', () => {
  const tenPercent: DiscountAuthority = { canApprove: false, maxPercent: 10, maxAmount: null };

  it('needs no approval within the user limit', () => {
    expect(requiresApproval(100, 1_000, tenPercent, 0)).toBe(false);
  });

  it('needs approval above the user limit', () => {
    expect(requiresApproval(100.01, 1_000, tenPercent, 0)).toBe(true);
  });

  it('applies the rupee cap when it is lower than the percentage', () => {
    const capped: DiscountAuthority = { canApprove: false, maxPercent: 20, maxAmount: 500 };
    expect(allowedWithoutApproval(10_000, capped)).toBe(500);
    expect(requiresApproval(600, 10_000, capped, 0)).toBe(true);
  });

  it('does not limit approvers', () => {
    expect(requiresApproval(900, 1_000, { canApprove: true, maxPercent: 100, maxAmount: null }, 0)).toBe(false);
  });

  it('lets a sanctioned amount cover the discount but not more', () => {
    expect(requiresApproval(300, 1_000, tenPercent, 300)).toBe(false);
    expect(requiresApproval(301, 1_000, tenPercent, 300)).toBe(true);
  });

  it('needs approval for every discount without a limit, but never for no discount', () => {
    expect(requiresApproval(1, 1_000, NO_DISCOUNT_AUTHORITY, 0)).toBe(true);
    expect(requiresApproval(0, 1_000, NO_DISCOUNT_AUTHORITY, 0)).toBe(false);
  });
});

describe('discount preview on a saved bill', () => {
  function savedLine(id: string, gross: number, options: Partial<PreviewLine> = {}): PreviewLine {
    return {
      id,
      description: `Service ${id}`,
      grossAmount: gross,
      packageCoveredAmount: 0,
      tariffDiscountAmount: 0,
      lineDiscountAmount: 0,
      isDiscountAllowed: true,
      taxRate: 0,
      isPayerPayable: true,
      ...options,
    };
  }

  function savedBill(lines: PreviewLine[], totals: Partial<PreviewBill> = {}): PreviewBill {
    return {
      lines,
      billDiscountPercent: null,
      billDiscountValue: null,
      payerType: 'Self',
      payerId: null,
      coPayPercent: 0,
      payerCoverageLimit: null,
      netAmount: 0,
      patientAmount: 0,
      payerAmount: 0,
      taxAmount: 0,
      discountAmount: 0,
      ...totals,
    };
  }

  it('recovers the tariff concession percentage from the saved amount', () => {
    expect(inferTariffPercent(savedLine('a', 1000, { tariffDiscountAmount: 100 }))).toBe(10);
    expect(inferTariffPercent(savedLine('b', 1000, { packageCoveredAmount: 600, tariffDiscountAmount: 40 }))).toBe(10);
    expect(inferTariffPercent(savedLine('c', -200, { tariffDiscountAmount: -20 }))).toBe(10);
    expect(inferTariffPercent(savedLine('d', 333.33, { tariffDiscountAmount: 41.67 }))).not.toBeNull();
    expect(inferTariffPercent(savedLine('e', 100, { isDiscountAllowed: false }))).toBe(0);
    expect(inferTariffPercent(savedLine('f', 100, { isDiscountAllowed: false, tariffDiscountAmount: 5 }))).toBeNull();
  });

  it('previews a bill discount with GST exactly like the server', () => {
    const bill = savedBill([savedLine('a', 1000, { taxRate: 18 })], {
      netAmount: 1180,
      patientAmount: 1180,
      taxAmount: 180,
    });
    const preview = previewDiscount(bill, { billPercent: 10, billAmount: null, lines: [] });
    expect(preview.ok).toBe(true);
    if (preview.ok) {
      expect(preview.totals.userDiscount).toBe(100);
      expect(preview.totals.patientAmount).toBe(1062);
    }
  });

  it('turns line percentages into rupees of the discountable value', () => {
    const bill = savedBill([savedLine('a', 1000, { tariffDiscountAmount: 100 }), savedLine('b', 250)], {
      netAmount: 1150,
      patientAmount: 1150,
      discountAmount: 100,
    });
    const preview = previewDiscount(bill, {
      billPercent: null,
      billAmount: null,
      lines: [{ lineId: 'a', percent: 10, amount: null }],
    });
    expect(preview.ok).toBe(true);
    if (preview.ok) {
      expect(preview.lineAmounts.get('a')).toBe(90);
      expect(preview.totals.patientAmount).toBe(1060);
    }
  });

  it('refuses a line discount larger than the line and discounts on protected services', () => {
    const bill = savedBill([savedLine('a', 100), savedLine('b', 50, { isDiscountAllowed: false })], {
      netAmount: 150,
      patientAmount: 150,
    });
    expect(
      previewDiscount(bill, {
        billPercent: null,
        billAmount: null,
        lines: [{ lineId: 'a', percent: null, amount: 101 }],
      }),
    ).toMatchObject({ ok: false, kind: 'invalid' });
    expect(
      previewDiscount(bill, {
        billPercent: null,
        billAmount: null,
        lines: [{ lineId: 'b', percent: null, amount: 1 }],
      }),
    ).toMatchObject({ ok: false, kind: 'invalid' });
  });

  it('switches itself off when the saved totals cannot be reproduced', () => {
    const bill = savedBill([savedLine('a', 1000)], { netAmount: 999, patientAmount: 999 });
    expect(previewDiscount(bill, { billPercent: 5, billAmount: null, lines: [] })).toMatchObject({
      ok: false,
      kind: 'unavailable',
    });
  });
});

describe('the GST a discount preview shows', () => {
  const totals = { tax: 180, cgst: 90, sgst: 90 };

  it('splits it into CGST and SGST within the state', () => {
    expect(gstBreakdown({ isInterState: false }, totals)).toEqual([
      { label: 'CGST', amount: 90 },
      { label: 'SGST', amount: 90 },
    ]);
  });

  it('shows it as IGST on a bill to another state, as the bill charges it', () => {
    expect(gstBreakdown({ isInterState: true }, totals)).toEqual([{ label: 'IGST', amount: 180 }]);
  });
});
