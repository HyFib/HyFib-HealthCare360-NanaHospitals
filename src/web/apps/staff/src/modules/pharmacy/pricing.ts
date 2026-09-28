import { dec, divRound, mul, round2, roundTo, sum, toNumber, type Dec } from '@/modules/inventory/decimal';
import { priceInclusive } from '@/modules/inventory/gst';

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
export function priceSaleLine(unitMrp: number, quantity: number, gstRate: number | null, discountPercent: number): SaleLinePrice {
  const priced = priceInclusive(quantity, unitMrp, discountPercent, gstRate ?? 0);
  if (gstRate === null) {
    return { gross: priced.gross, discount: priced.discount, taxable: null, cgst: null, sgst: null, net: priced.net };
  }
  return { gross: priced.gross, discount: priced.discount, taxable: priced.tax.taxable, cgst: priced.tax.cgst, sgst: priced.tax.sgst, net: priced.net };
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
export function refundFor(lineNet: number, lineQuantity: number, alreadyReturnedQuantity: number, alreadyRefunded: number, returnQuantity: number): number {
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
