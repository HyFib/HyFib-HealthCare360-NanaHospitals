import { isThirdPartyPayer } from './labels';
import {
  absBig,
  clampBig,
  fromPaise,
  maxBig,
  minBig,
  percentOfPaise,
  roundDiv,
  roundToRupee,
  splitGstPaise,
  toCentiPercent,
  toPaise,
} from './money';

/**
 * A line-for-line port of the server's `BillTotalsCalculator` and `DiscountPolicy`
 * (HyFib.HealthCare360.Modules.Billing.Domain). It exists only to preview a discount before it is sent; the server
 * recalculates every bill itself and its numbers are always the ones saved and printed. Amounts in and out are rupees
 * (as in the JSON); inside, everything is integer paise so the rounding matches C# `decimal` exactly.
 */

/** One bill line as the calculator sees it. `lineDiscount` is the staff discount in rupees. */
export interface BillLineInput {
  gross: number;
  packageCovered: number;
  tariffDiscountPercent: number;
  lineDiscount: number;
  discountAllowed: boolean;
  taxRate: number;
  payerPayable: boolean;
}

/** Bill-level discount: a percentage of the discountable value or a rupee amount (a percentage wins). */
export interface BillDiscountInput {
  percent: number | null;
  amount: number | null;
}

export const NO_BILL_DISCOUNT: BillDiscountInput = { percent: null, amount: null };

/** Payer terms: co-pay percentage and the payer's cap (`null` = no cap). */
export interface SplitTerms {
  hasPayer: boolean;
  coPayPercent: number;
  coverageLimit: number | null;
}

export const SELF_PAY: SplitTerms = { hasPayer: false, coPayPercent: 0, coverageLimit: null };

export interface BillLineResult {
  billable: number;
  tariffDiscount: number;
  lineDiscount: number;
  billDiscountShare: number;
  taxable: number;
  cgst: number;
  sgst: number;
  tax: number;
  net: number;
}

export interface BillTotals {
  lines: BillLineResult[];
  gross: number;
  packageCovered: number;
  tariffDiscount: number;
  lineDiscount: number;
  billDiscount: number;
  taxable: number;
  cgst: number;
  sgst: number;
  tax: number;
  roundOff: number;
  net: number;
  payerAmount: number;
  patientAmount: number;
  coPay: number;
  nonPayable: number;
  /** Tariff concessions plus staff discounts. */
  totalDiscount: number;
  /** Discount given by staff (line + bill); only this needs approval above a user's limit. */
  userDiscount: number;
  /** What staff discounts are measured against: billable value after package cover and tariff concessions. */
  discountBase: number;
}

interface LineWork {
  billable: bigint;
  tariff: bigint;
  lineDiscount: bigint;
  afterLine: bigint;
}

const WHOLE = 10_000n;
const sum = (values: readonly bigint[]) => values.reduce((total, value) => total + value, 0n);

function tariffFor(gross: bigint, billable: bigint, percent: bigint, discountAllowed: boolean): bigint {
  if (!discountAllowed) {
    return 0n;
  }
  const concession = percentOfPaise(billable, percent);
  return gross >= 0n ? minBig(concession, billable) : maxBig(concession, billable);
}

function billableFor(gross: bigint, covered: bigint): bigint {
  return gross >= 0n ? maxBig(gross - maxBig(covered, 0n), 0n) : minBig(gross - minBig(covered, 0n), 0n);
}

function allocateBillDiscount(
  lines: readonly BillLineInput[],
  afterLine: readonly bigint[],
  discount: BillDiscountInput,
): bigint[] {
  const shares = lines.map(() => 0n);
  // Credit lines are part of the basis with their negative value: the discount is a share of what was really supplied.
  const discountable = lines.map((_, i) => i).filter((i) => lines[i]!.discountAllowed && afterLine[i] !== 0n);
  const basis = sum(discountable.map((i) => afterLine[i]!));
  if (basis <= 0n) {
    return shares;
  }

  const requested =
    discount.percent !== null
      ? percentOfPaise(basis, clampBig(toCentiPercent(discount.percent), 0n, WHOLE))
      : toPaise(Math.max(discount.amount ?? 0, 0));
  const total = minBig(requested, basis);
  if (total <= 0n) {
    return shares;
  }

  let allocated = 0n;
  for (const i of discountable) {
    shares[i] = roundDiv(total * afterLine[i]!, basis);
    allocated += shares[i]!;
  }

  // Enumerable.MaxBy keeps the first of equal keys: the rounding remainder goes to the first largest line.
  let largest = discountable[0]!;
  for (const i of discountable) {
    if (afterLine[i]! > afterLine[largest]!) {
      largest = i;
    }
  }
  shares[largest] = clampBig(shares[largest]! + (total - allocated), 0n, afterLine[largest]!);
  return shares;
}

/** `BillTotalsCalculator.Calculate`. */
export function calculateBillTotals(
  lines: readonly BillLineInput[],
  discount: BillDiscountInput,
  terms: SplitTerms,
): BillTotals {
  const work: LineWork[] = lines.map((line) => {
    const gross = toPaise(line.gross);
    const tariffPercent = clampBig(toCentiPercent(line.tariffDiscountPercent), 0n, WHOLE);
    const billable = billableFor(gross, toPaise(line.packageCovered));
    const tariff = tariffFor(gross, billable, tariffPercent, line.discountAllowed);
    const lineDiscount =
      gross >= 0n && line.discountAllowed ? clampBig(toPaise(line.lineDiscount), 0n, billable - tariff) : 0n;
    return { billable, tariff, lineDiscount, afterLine: billable - tariff - lineDiscount };
  });

  const shares = allocateBillDiscount(
    lines,
    work.map((w) => w.afterLine),
    discount,
  );

  const results = work.map((w, i) => {
    const taxable = w.afterLine - shares[i]!;
    const rate = toCentiPercent(lines[i]!.taxRate);
    const tax = rate > 0n ? percentOfPaise(taxable, rate) : 0n;
    const { cgst, sgst } = splitGstPaise(tax);
    return {
      billable: w.billable,
      tariff: w.tariff,
      lineDiscount: w.lineDiscount,
      share: shares[i]!,
      taxable,
      cgst,
      sgst,
      tax,
      net: taxable + tax,
    };
  });

  const netBeforeRounding = sum(results.map((r) => r.net));
  const payable = terms.hasPayer ? sum(results.filter((_, i) => lines[i]!.payerPayable).map((r) => r.net)) : 0n;
  const coPay =
    terms.hasPayer && payable > 0n
      ? percentOfPaise(payable, clampBig(toCentiPercent(terms.coPayPercent), 0n, WHOLE))
      : 0n;
  const cap = terms.coverageLimit !== null ? maxBig(toPaise(terms.coverageLimit), 0n) : null;
  const payerShare = cap === null ? payable - coPay : minBig(payable - coPay, cap);
  const payerAmount = terms.hasPayer ? maxBig(payerShare, 0n) : 0n;

  const patientBeforeRounding = netBeforeRounding - payerAmount;
  const patientAmount = roundToRupee(patientBeforeRounding);
  const roundOff = patientAmount - patientBeforeRounding;

  const gross = sum(lines.map((line) => toPaise(line.gross)));
  const packageCovered = gross - sum(results.map((r) => r.billable));
  const tariffDiscount = sum(results.map((r) => r.tariff));
  const lineDiscount = sum(results.map((r) => r.lineDiscount));
  const billDiscount = sum(results.map((r) => r.share));

  return {
    lines: results.map((r) => ({
      billable: fromPaise(r.billable),
      tariffDiscount: fromPaise(r.tariff),
      lineDiscount: fromPaise(r.lineDiscount),
      billDiscountShare: fromPaise(r.share),
      taxable: fromPaise(r.taxable),
      cgst: fromPaise(r.cgst),
      sgst: fromPaise(r.sgst),
      tax: fromPaise(r.tax),
      net: fromPaise(r.net),
    })),
    gross: fromPaise(gross),
    packageCovered: fromPaise(packageCovered),
    tariffDiscount: fromPaise(tariffDiscount),
    lineDiscount: fromPaise(lineDiscount),
    billDiscount: fromPaise(billDiscount),
    taxable: fromPaise(sum(results.map((r) => r.taxable))),
    cgst: fromPaise(sum(results.map((r) => r.cgst))),
    sgst: fromPaise(sum(results.map((r) => r.sgst))),
    tax: fromPaise(sum(results.map((r) => r.tax))),
    roundOff: fromPaise(roundOff),
    net: fromPaise(payerAmount + patientAmount),
    payerAmount: fromPaise(payerAmount),
    patientAmount: fromPaise(patientAmount),
    coPay: fromPaise(coPay),
    nonPayable: fromPaise(terms.hasPayer ? netBeforeRounding - payable : 0n),
    totalDiscount: fromPaise(tariffDiscount + lineDiscount + billDiscount),
    userDiscount: fromPaise(lineDiscount + billDiscount),
    discountBase: fromPaise(gross - packageCovered - tariffDiscount),
  };
}

// ------------------------------------------------------------------ discount authority (DiscountPolicy)

/** How much discount a user may give on their own (GET /api/v1/billing/discount-limits/me). */
export interface DiscountAuthority {
  canApprove: boolean;
  maxPercent: number;
  maxAmount: number | null;
}

export const NO_DISCOUNT_AUTHORITY: DiscountAuthority = { canApprove: false, maxPercent: 0, maxAmount: 0 };

/** `DiscountPolicy.AllowedWithoutApproval`. */
export function allowedWithoutApproval(discountBase: number, authority: DiscountAuthority): number {
  const base = maxBig(toPaise(discountBase), 0n);
  if (authority.canApprove) {
    return fromPaise(base);
  }
  const limit = percentOfPaise(base, clampBig(toCentiPercent(authority.maxPercent), 0n, WHOLE));
  return fromPaise(authority.maxAmount !== null ? minBig(limit, maxBig(toPaise(authority.maxAmount), 0n)) : limit);
}

/** `DiscountPolicy.RequiresApproval`. */
export function requiresApproval(
  userDiscount: number,
  discountBase: number,
  authority: DiscountAuthority,
  approvedAmount: number,
): boolean {
  const discount = toPaise(userDiscount);
  return (
    discount > 0n &&
    !authority.canApprove &&
    discount > toPaise(approvedAmount) &&
    discount > toPaise(allowedWithoutApproval(discountBase, authority))
  );
}

// ------------------------------------------------------------------ preview of a discount on a saved bill

/** The fields of a bill line the preview needs (a subset of BillLineDto). */
export interface PreviewLine {
  id: string;
  description: string;
  grossAmount: number;
  packageCoveredAmount: number;
  tariffDiscountAmount: number;
  lineDiscountAmount: number;
  isDiscountAllowed: boolean;
  taxRate: number;
  isPayerPayable: boolean;
}

/** The fields of a bill the preview needs (a subset of BillDetail). */
export interface PreviewBill {
  lines: readonly PreviewLine[];
  billDiscountPercent: number | null;
  billDiscountValue: number | null;
  payerType: string;
  payerId: string | null;
  coPayPercent: number;
  payerCoverageLimit: number | null;
  netAmount: number;
  patientAmount: number;
  payerAmount: number;
  taxAmount: number;
  discountAmount: number;
}

export interface LineDiscountRequest {
  lineId: string;
  percent: number | null;
  amount: number | null;
}

export interface DiscountDraft {
  billPercent: number | null;
  billAmount: number | null;
  lines: readonly LineDiscountRequest[];
}

export type DiscountPreview =
  | { ok: true; totals: BillTotals; lineAmounts: ReadonlyMap<string, number> }
  | { ok: false; kind: 'unavailable' | 'invalid'; message: string };

/** `BillLine.DiscountableAmount`: the value staff discounts are measured against on a line. */
export function lineDiscountable(line: PreviewLine): number {
  if (!line.isDiscountAllowed) {
    return 0;
  }
  return fromPaise(
    maxBig(toPaise(line.grossAmount) - toPaise(line.packageCoveredAmount) - toPaise(line.tariffDiscountAmount), 0n),
  );
}

/**
 * The tariff concession percentage of a saved line. The API returns the concession amount, not the percentage; any
 * percentage that reproduces the amount gives the same result, because the concession never depends on staff
 * discounts. Returns null when no percentage fits (the preview is then switched off).
 */
export function inferTariffPercent(line: PreviewLine): number | null {
  const gross = toPaise(line.grossAmount);
  const billable = billableFor(gross, toPaise(line.packageCoveredAmount));
  const target = toPaise(line.tariffDiscountAmount);
  if (!line.isDiscountAllowed || billable === 0n) {
    return target === 0n ? 0 : null;
  }
  const estimate = roundDiv(absBig(target) * WHOLE, absBig(billable));
  for (const candidate of [estimate, estimate - 1n, estimate + 1n, WHOLE]) {
    if (candidate >= 0n && candidate <= WHOLE && tariffFor(gross, billable, candidate, true) === target) {
      return Number(candidate) / 100;
    }
  }
  return null;
}

export function splitTermsOf(
  bill: Pick<PreviewBill, 'payerType' | 'payerId' | 'coPayPercent' | 'payerCoverageLimit'>,
): SplitTerms {
  return {
    hasPayer: isThirdPartyPayer(bill.payerType, bill.payerId),
    coPayPercent: bill.coPayPercent,
    coverageLimit: bill.payerCoverageLimit,
  };
}

/**
 * Totals the bill would have with the requested staff discounts (`DiscountService.Preview`). The preview first
 * recalculates the bill as saved and compares it with the server's own totals; if they differ in any paisa it
 * reports "unavailable" instead of guessing, and the server's answer is shown after applying.
 */
export function previewDiscount(bill: PreviewBill, draft: DiscountDraft): DiscountPreview {
  const percents = bill.lines.map(inferTariffPercent);
  if (percents.some((p) => p === null)) {
    return {
      ok: false,
      kind: 'unavailable',
      message: 'A live preview is not available for this bill. The new total appears after you apply the discount.',
    };
  }
  const baseInputs: BillLineInput[] = bill.lines.map((line, i) => ({
    gross: line.grossAmount,
    packageCovered: line.packageCoveredAmount,
    tariffDiscountPercent: percents[i]!,
    lineDiscount: line.lineDiscountAmount,
    discountAllowed: line.isDiscountAllowed,
    taxRate: line.taxRate,
    payerPayable: line.isPayerPayable,
  }));
  const terms = splitTermsOf(bill);

  const baseline = calculateBillTotals(
    baseInputs,
    { percent: bill.billDiscountPercent, amount: bill.billDiscountValue },
    terms,
  );
  const matches =
    toPaise(baseline.net) === toPaise(bill.netAmount) &&
    toPaise(baseline.patientAmount) === toPaise(bill.patientAmount) &&
    toPaise(baseline.payerAmount) === toPaise(bill.payerAmount) &&
    toPaise(baseline.tax) === toPaise(bill.taxAmount) &&
    toPaise(baseline.totalDiscount) === toPaise(bill.discountAmount);
  if (!matches) {
    return {
      ok: false,
      kind: 'unavailable',
      message: 'A live preview is not available for this bill. The new total appears after you apply the discount.',
    };
  }

  // DiscountService.ResolveLineDiscounts: percentages become rupees of the line's discountable value.
  const lineAmounts = new Map<string, number>();
  for (const request of draft.lines) {
    const line = bill.lines.find((l) => l.id === request.lineId);
    if (!line) {
      return { ok: false, kind: 'invalid', message: 'A discounted service is not on this bill.' };
    }
    if (!line.isDiscountAllowed) {
      return { ok: false, kind: 'invalid', message: `${line.description} cannot be discounted.` };
    }
    const discountable = toPaise(lineDiscountable(line));
    const amount =
      request.percent !== null
        ? percentOfPaise(discountable, clampBig(toCentiPercent(request.percent), 0n, WHOLE))
        : toPaise(Math.max(request.amount ?? 0, 0));
    if (amount > discountable) {
      return { ok: false, kind: 'invalid', message: `The discount on ${line.description} is more than its value.` };
    }
    if (amount > 0n) {
      lineAmounts.set(line.id, fromPaise(amount));
    }
  }

  const inputs = baseInputs.map((input, i) => ({ ...input, lineDiscount: lineAmounts.get(bill.lines[i]!.id) ?? 0 }));
  const discount: BillDiscountInput =
    draft.billPercent !== null && draft.billPercent > 0
      ? { percent: draft.billPercent, amount: null }
      : { percent: null, amount: draft.billAmount };
  return { ok: true, totals: calculateBillTotals(inputs, discount, terms), lineAmounts };
}

/** What can still be credited on a finalized bill: the patient share less earlier credit notes (`Bill.ApplyCredit`). */
export function creditableAmount(bill: { patientAmount: number; creditAmount: number }): number {
  return fromPaise(maxBig(toPaise(bill.patientAmount) - toPaise(bill.creditAmount), 0n));
}

/**
 * How the GST in a preview is shown: as IGST on a bill whose place of supply is another state (it already charges
 * IGST, and a discount does not change where the supply goes), otherwise as CGST and SGST.
 */
export function gstBreakdown(
  bill: { igstAmount: number; lines: readonly { igstAmount: number }[] },
  totals: { tax: number; cgst: number; sgst: number },
): { label: 'CGST' | 'SGST' | 'IGST'; amount: number }[] {
  const interState = bill.igstAmount !== 0 || bill.lines.some((line) => line.igstAmount !== 0);
  return interState
    ? [{ label: 'IGST', amount: totals.tax }]
    : [
        { label: 'CGST', amount: totals.cgst },
        { label: 'SGST', amount: totals.sgst },
      ];
}
