import { formatInr } from '@hc360/core';
import { dec, divRound, mul, round2, roundTo, sum, toNumber, type Dec } from '@/modules/inventory/decimal';
import { priceInclusive } from '@/modules/inventory/gst';
import type { PaymentMode, PharmacyPaymentDto } from './api';

/**
 * Client mirror of the server's SalePricing (Pharmacy/Domain/SalePricing.cs) for the live bill while a counter
 * sale or a prescription is being made up. Medicines sell at MRP, which includes GST; a discount comes off the
 * MRP value; GST is carved out and split CGST = SGST; the bill rounds to the rupee with a printed round-off.
 *
 * The net amount never depends on the GST rate (GST sits inside the MRP), so the payable amount is exact even
 * when the workstation cannot see the item master's GST rate; the CGST/SGST breakup is then left to the invoice.
 */

export interface SaleLinePrice {
  gross: number;
  discount: number;
  /** null when the GST rate is not known on this workstation. */
  taxable: number | null;
  cgst: number | null;
  sgst: number | null;
  net: number;
}

export interface SaleTotals {
  gross: number;
  discount: number;
  taxable: number | null;
  cgst: number | null;
  sgst: number | null;
  roundOff: number;
  /** Payable, rounded to the rupee. */
  net: number;
  /** Every line's GST rate was known, so the breakup matches what the invoice will print. */
  gstKnown: boolean;
}

/** SalePricing.PriceLine: one batch of one item at its MRP. */
export function priceSaleLine(
  unitMrp: number,
  quantity: number,
  gstRate: number | null,
  discountPercent: number,
): SaleLinePrice {
  const priced = priceInclusive(quantity, unitMrp, discountPercent, gstRate ?? 0);
  if (gstRate === null) {
    return { gross: priced.gross, discount: priced.discount, taxable: null, cgst: null, sgst: null, net: priced.net };
  }
  return {
    gross: priced.gross,
    discount: priced.discount,
    taxable: priced.tax.taxable,
    cgst: priced.tax.cgst,
    sgst: priced.tax.sgst,
    net: priced.net,
  };
}

/** SalePricing.Total: sums the lines and rounds the bill to the rupee. */
export function saleTotals(lines: readonly SaleLinePrice[]): SaleTotals {
  const add = (values: number[]) => sum(values.map((v) => dec(v)));
  const net = add(lines.map((l) => l.net));
  const rounded = roundTo(net, 0);
  const gstKnown = lines.every((l) => l.taxable !== null && l.cgst !== null && l.sgst !== null);
  return {
    gross: toNumber(add(lines.map((l) => l.gross))),
    discount: toNumber(add(lines.map((l) => l.discount))),
    taxable: gstKnown ? toNumber(add(lines.map((l) => l.taxable ?? 0))) : null,
    cgst: gstKnown ? toNumber(add(lines.map((l) => l.cgst ?? 0))) : null,
    sgst: gstKnown ? toNumber(add(lines.map((l) => l.sgst ?? 0))) : null,
    roundOff: toNumber(rounded - net),
    net: toNumber(rounded),
    gstKnown,
  };
}

/** Line amount for an IP issue: quantity × batch MRP, rounded to the paisa (no discount, charged to the IP bill). */
export function ipIssueAmount(quantity: number, unitMrp: number): number {
  return toNumber(round2(mul(dec(quantity), dec(unitMrp))));
}

/**
 * SalePricing.RefundFor: refund for returning part of an invoice line. The last return of a line refunds exactly
 * what is left, so repeated partial returns never drift by a paisa from what the patient paid.
 */
export function refundFor(
  lineNet: number,
  lineQuantity: number,
  alreadyReturnedQuantity: number,
  alreadyRefunded: number,
  returnQuantity: number,
): number {
  const quantity: Dec = dec(lineQuantity);
  const returning: Dec = dec(returnQuantity);
  if (quantity <= 0n || returning <= 0n) {
    return 0;
  }
  if (dec(alreadyReturnedQuantity) + returning >= quantity) {
    return toNumber(dec(lineNet) - dec(alreadyRefunded));
  }
  return toNumber(divRound(mul(dec(lineNet), returning), quantity, 2));
}

/** One part of the money a cancellation or a return pays back: to the payment it came in by, in its mode. */
export interface RefundPart {
  paymentId: string;
  mode: PaymentMode;
  amount: number;
  reference: string | null;
}

const MODE_ORDER: Record<PaymentMode, number> = { Cash: 0, Card: 1, Upi: 2, Credit: 3 };

/**
 * How `amount` goes back, as the server pays it (SaleRefunds): to the payments it came in by, in their own modes, never
 * cash for a card or UPI payment. Each payment can give back what it brought less what was paid back against it (refunds
 * that name no payment come off the last payments first). A partial refund is shared in proportion to what each has
 * left, to the paisa: each share rounded down, the paise left over to the largest remainders (the earlier first).
 * Payments taken together at the counter count cash first, then card, then UPI.
 */
export function refundSplit(payments: readonly PharmacyPaymentDto[], amount: number): RefundPart[] {
  const taken = payments
    .filter((p) => !p.isRefund && p.mode !== 'Credit')
    .sort(
      (a, b) =>
        Date.parse(a.receivedAt) - Date.parse(b.receivedAt) ||
        MODE_ORDER[a.mode] - MODE_ORDER[b.mode] ||
        (a.id < b.id ? -1 : a.id > b.id ? 1 : 0),
    );
  const refunds = payments.filter((p) => p.isRefund);
  const paise = (value: number) => round2(dec(value)) / 10n ** 6n;
  const left = taken.map(
    (p) =>
      paise(p.amount) - refunds.filter((r) => r.refundOfPaymentId === p.id).reduce((s, r) => s + paise(r.amount), 0n),
  );
  const positive = (value: bigint) => (value > 0n ? value : 0n);
  let unnamed = refunds.filter((r) => !r.refundOfPaymentId).reduce((s, r) => s + paise(r.amount), 0n);
  for (let i = taken.length - 1; i >= 0 && unnamed > 0n; i--) {
    const off = positive(left[i]!) < unnamed ? positive(left[i]!) : unnamed;
    left[i] = left[i]! - off;
    unnamed -= off;
  }
  const open = taken.map((p, i) => ({ payment: p, left: positive(left[i]!) })).filter((p) => p.left > 0n);
  const total = open.reduce((s, p) => s + p.left, 0n);
  let wanted = paise(amount);
  if (wanted > total) {
    wanted = total;
  }
  if (wanted <= 0n) {
    return [];
  }
  const shares = open.map((p) => (p.left * wanted) / total);
  const remainders = open.map((p) => (p.left * wanted) % total);
  let over = wanted - shares.reduce((s, x) => s + x, 0n);
  const byRemainder = open
    .map((_, i) => i)
    .sort((a, b) => (remainders[b]! > remainders[a]! ? 1 : remainders[b]! < remainders[a]! ? -1 : a - b));
  for (const i of byRemainder) {
    if (over === 0n) {
      break;
    }
    if (shares[i]! < open[i]!.left) {
      shares[i] = shares[i]! + 1n;
      over -= 1n;
    }
  }
  return open
    .map((p, i) => ({
      paymentId: p.payment.id,
      mode: p.payment.mode,
      amount: Number(shares[i]!) / 100,
      reference: p.payment.reference,
    }))
    .filter((p) => p.amount > 0);
}

/** How the money goes back, in words, as the approver reads it: "₹60.00 back to the card (RRN 627910123456), ₹40.00 in cash". */
export function refundWords(parts: readonly RefundPart[]): string {
  if (parts.length === 0) {
    return 'Nothing is paid back';
  }
  return parts
    .map((p) =>
      p.mode === 'Card'
        ? `${formatInr(p.amount)} back to the card${p.reference ? ` (RRN or approval code ${p.reference})` : ''}`
        : p.mode === 'Upi'
          ? `${formatInr(p.amount)} back by UPI${p.reference ? ` (UTR ${p.reference})` : ''}`
          : `${formatInr(p.amount)} in cash`,
    )
    .join(', ');
}
