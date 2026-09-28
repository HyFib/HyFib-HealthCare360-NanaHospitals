/**
 * Canteen GST arithmetic, mirroring the server's `CanteenBillCalculator` so the POS shows exactly the bill the server
 * will make. Tax is worked out per line — backed out of GST-inclusive prices (taxable = amount × 100 ÷ (100 + rate))
 * or added to exclusive ones — rounded to the paisa; the GST is split into CGST and SGST with CGST = round(GST ÷ 2)
 * (half away from zero, so an odd paisa goes to CGST, exactly as the server's `Money.SplitGst` does); the bill is
 * rounded to the nearest rupee with a round-off line.
 *
 * All arithmetic is done in integer paise, so there is no floating-point drift.
 */

export interface BillLineInput {
  menuItemId: string;
  itemName: string;
  quantity: number;
  /** Rupees with at most two decimals. */
  unitPrice: number;
  /** GST slab: 0, 5, 12, 18 or 28. */
  gstRate: number;
  priceIncludesGst: boolean;
}

export interface BillLine extends BillLineInput {
  taxableAmount: number;
  cgst: number;
  sgst: number;
  lineTotal: number;
}

export interface BillTotals {
  lines: BillLine[];
  taxableAmount: number;
  cgst: number;
  sgst: number;
  totalGst: number;
  grossAmount: number;
  roundOff: number;
  netAmount: number;
}

/** Integer division rounded half away from zero (numerator and denominator are integers, denominator > 0). */
function divRound(numerator: number, denominator: number): number {
  const sign = numerator < 0 ? -1 : 1;
  const n = Math.abs(numerator);
  const quotient = Math.floor(n / denominator);
  const remainder = n - quotient * denominator;
  return sign * (remainder * 2 >= denominator ? quotient + 1 : quotient);
}

export function toPaise(rupees: number): number {
  return Math.round(rupees * 100);
}

export function toRupees(paise: number): number {
  return paise / 100;
}

/** GST rates are whole-number slabs; a fractional rate is scaled to keep the arithmetic in integers. */
function rateParts(rate: number): { numerator: number; scale: number } {
  const scale = Number.isInteger(rate) ? 1 : 100;
  return { numerator: Math.round(rate * scale), scale };
}

export function calculateLine(line: BillLineInput): BillLine {
  if (!Number.isInteger(line.quantity) || line.quantity <= 0) {
    throw new RangeError('Quantity must be a positive whole number.');
  }
  const amount = toPaise(line.unitPrice) * line.quantity;
  const { numerator: rate, scale } = rateParts(line.gstRate);
  let taxable: number;
  let gst: number;
  if (line.priceIncludesGst) {
    taxable = divRound(amount * 100 * scale, 100 * scale + rate);
    gst = amount - taxable;
  } else {
    taxable = amount;
    gst = divRound(taxable * rate, 100 * scale);
  }
  const cgst = divRound(gst, 2);
  const sgst = gst - cgst;
  return { ...line, taxableAmount: toRupees(taxable), cgst: toRupees(cgst), sgst: toRupees(sgst), lineTotal: toRupees(taxable + gst) };
}

export function calculateBill(lines: readonly BillLineInput[]): BillTotals {
  const billLines = lines.map(calculateLine);
  const sum = (pick: (l: BillLine) => number) => billLines.reduce((total, l) => total + toPaise(pick(l)), 0);
  const gross = sum((l) => l.lineTotal);
  const net = divRound(gross, 100) * 100;
  const cgst = sum((l) => l.cgst);
  const sgst = sum((l) => l.sgst);
  return {
    lines: billLines,
    taxableAmount: toRupees(sum((l) => l.taxableAmount)),
    cgst: toRupees(cgst),
    sgst: toRupees(sgst),
    totalGst: toRupees(cgst + sgst),
    grossAmount: toRupees(gross),
    roundOff: toRupees(net - gross),
    netAmount: toRupees(net),
  };
}

/** Quantities per item from a cart (the server merges repeated items into one line the same way). */
export function mergeCart<T extends { menuItemId: string; quantity: number }>(lines: readonly T[]): T[] {
  const merged = new Map<string, T>();
  for (const line of lines) {
    const current = merged.get(line.menuItemId);
    merged.set(line.menuItemId, current ? { ...current, quantity: current.quantity + line.quantity } : { ...line });
  }
  return [...merged.values()];
}

/** Change to return for a cash payment, in rupees (never negative). */
export function changeDue(netAmount: number, tendered: number): number {
  return Math.max(0, toRupees(toPaise(tendered) - toPaise(netAmount)));
}
