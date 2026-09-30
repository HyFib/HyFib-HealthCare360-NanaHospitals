import { dec, divRound, HUNDRED, mul, round2, roundTo, sum, toNumber, type Dec } from './decimal';

/**
 * Client mirror of the server's GstCalculator (SupplyChain/Domain/GstCalculator.cs), used only for live previews
 * while a cart, order or goods receipt is being typed. Saved documents always show the amounts the server returns.
 *
 * - Purchases are priced exclusive of GST: taxable = quantity × rate − discount, GST added on top.
 * - Medicines sell at MRP, which includes GST: the tax is carved out, taxable = amount × 100 ÷ (100 + rate).
 * - Within a state GST is split equally into CGST and SGST (SGST takes any odd paisa); between states it is IGST.
 * - Money rounds to the paisa half away from zero; invoice totals round to the rupee with a printed round-off.
 */

/** Notified GST slabs, including the 2025 rationalised 5% / 18% / 40% structure and legacy slabs. */
export const GST_RATES = [0, 0.25, 3, 5, 12, 18, 28, 40] as const;

export function isStandardGstRate(rate: number): boolean {
  return (GST_RATES as readonly number[]).includes(rate);
}

export interface TaxBreakup {
  taxable: number;
  gst: number;
  cgst: number;
  sgst: number;
  igst: number;
  /** taxable + gst */
  total: number;
}

export interface PricedLine {
  gross: number;
  discount: number;
  tax: TaxBreakup;
  /** What the line costs including GST (= tax.total). */
  net: number;
}

interface ExactTax {
  taxable: Dec;
  gst: Dec;
  cgst: Dec;
  sgst: Dec;
  igst: Dec;
}

function percentOf(amount: Dec, percent: Dec): Dec {
  return divRound(mul(amount, percent), HUNDRED, 2);
}

function split(taxable: Dec, gst: Dec, interState: boolean): ExactTax {
  if (interState) {
    return { taxable, gst, cgst: 0n, sgst: 0n, igst: gst };
  }
  const cgst = divRound(gst, dec(2), 2);
  return { taxable, gst, cgst, sgst: round2(gst - cgst), igst: 0n };
}

function toBreakup(tax: ExactTax): TaxBreakup {
  return {
    taxable: toNumber(tax.taxable),
    gst: toNumber(tax.gst),
    cgst: toNumber(tax.cgst),
    sgst: toNumber(tax.sgst),
    igst: toNumber(tax.igst),
    total: toNumber(tax.taxable + tax.gst),
  };
}

/** GST on a value that excludes tax (vendor invoices and purchase orders). */
export function gstOnExclusive(taxableAmount: number, ratePercent: number, interState: boolean): TaxBreakup {
  const taxable = round2(dec(taxableAmount));
  return toBreakup(split(taxable, percentOf(taxable, dec(ratePercent)), interState));
}

/** GST contained in a tax-inclusive price such as MRP. */
export function gstFromInclusive(inclusiveAmount: number, ratePercent: number, interState = false): TaxBreakup {
  return toBreakup(fromInclusiveExact(dec(inclusiveAmount), dec(ratePercent), interState));
}

function fromInclusiveExact(inclusive: Dec, rate: Dec, interState: boolean): ExactTax {
  const total = round2(inclusive);
  const taxable = divRound(mul(total, HUNDRED), HUNDRED + rate, 2);
  return split(taxable, total - taxable, interState);
}

/** Purchase line: quantity × rate, less trade discount, plus GST on the discounted value. */
export function priceExclusive(
  quantity: number,
  rate: number,
  discountPercent: number,
  gstRate: number,
  interState: boolean,
): PricedLine {
  const gross = round2(mul(dec(quantity), dec(rate)));
  const discount = percentOf(gross, dec(discountPercent));
  const tax = split(round2(gross - discount), percentOf(round2(gross - discount), dec(gstRate)), interState);
  return {
    gross: toNumber(gross),
    discount: toNumber(discount),
    tax: toBreakup(tax),
    net: toNumber(tax.taxable + tax.gst),
  };
}

/** Retail line at MRP: quantity × unit MRP, less discount; GST is carved out of what remains. */
export function priceInclusive(
  quantity: number,
  unitMrp: number,
  discountPercent: number,
  gstRate: number,
): PricedLine {
  const gross = round2(mul(dec(quantity), dec(unitMrp)));
  const discount = percentOf(gross, dec(discountPercent));
  const tax = fromInclusiveExact(gross - discount, dec(gstRate), false);
  return {
    gross: toNumber(gross),
    discount: toNumber(discount),
    tax: toBreakup(tax),
    net: toNumber(tax.taxable + tax.gst),
  };
}

/** Rounds an invoice total to the nearest rupee; Indian invoices print the difference as "round off". */
export function roundToRupee(amount: number): { rounded: number; roundOff: number } {
  const exact = dec(amount);
  const rounded = roundTo(exact, 0);
  return { rounded: toNumber(rounded), roundOff: toNumber(rounded - exact) };
}

export interface DocumentTotals {
  gross: number;
  discount: number;
  taxable: number;
  cgst: number;
  sgst: number;
  igst: number;
  roundOff: number;
  /** Rounded to the rupee. */
  net: number;
}

/** Totals of a purchase order or goods receipt: GST per line, summed, net rounded to the rupee. */
export function exclusiveTotals(lines: readonly PricedLine[]): DocumentTotals {
  const add = (pick: (line: PricedLine) => number) => sum(lines.map((line) => dec(pick(line))));
  const taxable = add((l) => l.tax.taxable);
  const cgst = add((l) => l.tax.cgst);
  const sgst = add((l) => l.tax.sgst);
  const igst = add((l) => l.tax.igst);
  const unrounded = taxable + cgst + sgst + igst;
  const rounded = roundTo(unrounded, 0);
  return {
    gross: toNumber(add((l) => l.gross)),
    discount: toNumber(add((l) => l.discount)),
    taxable: toNumber(taxable),
    cgst: toNumber(cgst),
    sgst: toNumber(sgst),
    igst: toNumber(igst),
    roundOff: toNumber(rounded - unrounded),
    net: toNumber(rounded),
  };
}

/** GST state code: the first two digits of a GSTIN (e.g. 27 = Maharashtra). */
export function gstStateCode(gstin: string | null | undefined): string | null {
  const value = gstin?.trim();
  return value && value.length >= 2 ? value.slice(0, 2) : null;
}

/**
 * Inter-state supply (IGST) when both GSTINs are known and their state codes differ — exactly the server's
 * `Vendor.IsInterState`. Unknown on either side means intra-state (CGST + SGST).
 */
export function isInterState(
  vendorGstin: string | null | undefined,
  facilityGstin: string | null | undefined,
): boolean {
  const vendor = gstStateCode(vendorGstin);
  const facility = gstStateCode(facilityGstin);
  return vendor !== null && facility !== null && vendor !== facility;
}

/** Indian state names for GST state codes (for "Maharashtra (27)" style explanations). */
export const GST_STATES: Record<string, string> = {
  '01': 'Jammu and Kashmir',
  '02': 'Himachal Pradesh',
  '03': 'Punjab',
  '04': 'Chandigarh',
  '05': 'Uttarakhand',
  '06': 'Haryana',
  '07': 'Delhi',
  '08': 'Rajasthan',
  '09': 'Uttar Pradesh',
  '10': 'Bihar',
  '11': 'Sikkim',
  '12': 'Arunachal Pradesh',
  '13': 'Nagaland',
  '14': 'Manipur',
  '15': 'Mizoram',
  '16': 'Tripura',
  '17': 'Meghalaya',
  '18': 'Assam',
  '19': 'West Bengal',
  '20': 'Jharkhand',
  '21': 'Odisha',
  '22': 'Chhattisgarh',
  '23': 'Madhya Pradesh',
  '24': 'Gujarat',
  '26': 'Dadra and Nagar Haveli and Daman and Diu',
  '27': 'Maharashtra',
  '29': 'Karnataka',
  '30': 'Goa',
  '31': 'Lakshadweep',
  '32': 'Kerala',
  '33': 'Tamil Nadu',
  '34': 'Puducherry',
  '35': 'Andaman and Nicobar Islands',
  '36': 'Telangana',
  '37': 'Andhra Pradesh',
  '38': 'Ladakh',
};

export function gstStateName(gstin: string | null | undefined): string | null {
  const code = gstStateCode(gstin);
  return code ? `${GST_STATES[code] ?? 'State'} (${code})` : null;
}
