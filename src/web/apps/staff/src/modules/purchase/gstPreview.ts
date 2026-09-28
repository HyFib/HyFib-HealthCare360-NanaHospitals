import { dec, divRound, mul, toNumber, type Dec } from '@/modules/inventory/decimal';
import { addDays } from '@/modules/inventory/format';
import { exclusiveTotals, gstStateName, isInterState, priceExclusive, type DocumentTotals, type PricedLine } from '@/modules/inventory/gst';
import type { ItemType } from '@/modules/inventory/labels';

/**
 * Live GST preview for purchase orders and goods receipts, mirroring the server (PurchaseOrder.Recalculate,
 * GoodsReceiptNote.Recalculate): each line is quantity × rate less trade discount, GST is added on the discounted
 * value, split CGST + SGST within a state or IGST between states, and the document total rounds to the rupee.
 * Saved documents always show the server's own figures.
 */

export interface PurchaseLineDraft {
  quantity: number | null;
  rate: number | null;
  discountPercent: number | null;
  gstRate: number | null;
}

export function previewLine(line: PurchaseLineDraft, interState: boolean): PricedLine {
  return priceExclusive(line.quantity ?? 0, line.rate ?? 0, line.discountPercent ?? 0, line.gstRate ?? 0, interState);
}

export function previewTotals(lines: readonly PurchaseLineDraft[], interState: boolean): DocumentTotals {
  return exclusiveTotals(lines.map((line) => previewLine(line, interState)));
}

export interface GstSplit {
  interState: boolean;
  /** Both GSTINs are known, so the split shown is the one the server will apply. */
  known: boolean;
  headline: string;
  detail: string;
}

/** Plain-language explanation of CGST + SGST versus IGST for a vendor and this hospital. */
export function explainGstSplit(vendorGstin: string | null | undefined, facilityGstin: string | null | undefined): GstSplit {
  const vendorState = gstStateName(vendorGstin);
  const facilityState = gstStateName(facilityGstin);
  const interState = isInterState(vendorGstin, facilityGstin);
  if (!vendorState) {
    return {
      interState: false,
      known: true,
      headline: 'CGST + SGST (same state)',
      detail: 'The vendor has no GSTIN on record, so GST is treated as a supply within the state: half CGST, half SGST.',
    };
  }
  if (!facilityState) {
    return {
      interState: false,
      known: false,
      headline: 'CGST + SGST (assumed)',
      detail: "The hospital's GSTIN is not visible here, so the preview assumes a supply within the state. The saved document shows the final split.",
    };
  }
  return interState
    ? {
        interState: true,
        known: true,
        headline: 'IGST (inter-state supply)',
        detail: `The vendor is in ${vendorState} and the hospital in ${facilityState}. Supplies between states carry the full GST as IGST.`,
      }
    : {
        interState: false,
        known: true,
        headline: 'CGST + SGST (same state)',
        detail: `Vendor and hospital are both in ${facilityState}. GST is split equally: half CGST to the Centre, half SGST to the State.`,
      };
}

/** Items whose receipts must carry batch and expiry (Item.TracksExpiry). */
export function tracksExpiry(type: ItemType): boolean {
  return type === 'Drug' || type === 'Consumable' || type === 'Surgical' || type === 'Implant' || type === 'Food';
}

export interface GrnLineDraft {
  itemName: string;
  itemType: ItemType;
  unitsPerPurchaseUnit: number;
  batchNumber: string;
  manufactureDate: string | null;
  expiryDate: string | null;
  quantity: number | null;
  freeQuantity: number | null;
  rate: number | null;
  mrp: number | null;
  discountPercent: number | null;
  gstRate: number | null;
  acceptShortExpiry: boolean;
}

export const SHORT_EXPIRY_DAYS = 90;

/** GoodsReceiptLine.Validate in plain language (the first blocking problem), or null when the line is fine. */
export function grnLineProblem(line: GrnLineDraft, receivedOn: string): string | null {
  const quantity = line.quantity ?? 0;
  const free = line.freeQuantity ?? 0;
  if (quantity < 0 || free < 0 || quantity + free <= 0) {
    return 'Enter the received quantity.';
  }
  const needsBatch = tracksExpiry(line.itemType);
  if (needsBatch && !line.batchNumber.trim()) {
    return 'Enter the batch number printed on the pack.';
  }
  if (needsBatch && !line.expiryDate) {
    return 'Enter the expiry date.';
  }
  if (line.expiryDate) {
    if (line.expiryDate <= receivedOn) {
      return 'This batch has expired. Do not accept expired goods.';
    }
    if (line.expiryDate < addDays(receivedOn, SHORT_EXPIRY_DAYS) && !line.acceptShortExpiry) {
      return `Expires within ${SHORT_EXPIRY_DAYS} days. Tick "Accept short expiry" only if the store agreed to take it.`;
    }
    if (line.manufactureDate && line.manufactureDate >= line.expiryDate) {
      return 'The manufacturing date must be before the expiry date.';
    }
  }
  if (line.itemType === 'Drug' && !((line.mrp ?? 0) > 0)) {
    return 'Enter the MRP printed on the pack.';
  }
  if ((line.mrp ?? 0) > 0 && (line.rate ?? 0) > (line.mrp ?? 0)) {
    return 'The purchase rate is above the MRP. Check the vendor invoice.';
  }
  return null;
}

/** True when the expiry falls within the short-expiry window (acceptance needed). */
export function isShortExpiry(expiryDate: string | null, receivedOn: string): boolean {
  return Boolean(expiryDate && expiryDate > receivedOn && expiryDate < addDays(receivedOn, SHORT_EXPIRY_DAYS));
}

export interface GrnUnitEconomics {
  /** Stock added in base units (paid + free). */
  baseQuantity: number;
  /** Cost per base unit after spreading the taxable value over paid and free units. */
  unitCost: number;
  /** MRP per base unit. */
  unitMrp: number;
}

/** GoodsReceiptLine.BaseQuantity, UnitCost and UnitMrp (4 decimals, half away from zero). */
export function grnUnitEconomics(quantity: number, freeQuantity: number, unitsPerPurchaseUnit: number, taxableAmount: number, mrp: number): GrnUnitEconomics {
  const factor: Dec = dec(unitsPerPurchaseUnit);
  const base: Dec = mul(dec(quantity) + dec(freeQuantity), factor);
  return {
    baseQuantity: toNumber(base),
    unitCost: base > 0n ? toNumber(divRound(dec(taxableAmount), base, 4)) : 0,
    unitMrp: factor > 0n ? toNumber(divRound(dec(mrp), factor, 4)) : 0,
  };
}
