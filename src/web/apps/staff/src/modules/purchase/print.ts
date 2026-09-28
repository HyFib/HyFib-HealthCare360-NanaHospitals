import { escapeHtml, formatDateOnly, formatDateTime, formatInr, printDocument, printHtml } from '@hc360/core';
import { formatExpiry, formatPercent, formatQty } from '@/modules/inventory/format';
import type { GoodsReceiptDetail, PurchaseOrderDetail } from './api';

export interface PurchaseLetterhead {
  name: string;
  address?: string | null;
  phone?: string | null;
  gstin?: string | null;
}

const STYLES = `
  .hospital { font-weight: 700; font-size: 16px; }
  .small { font-size: 10px; color: #444; }
  .meta td { padding: 2px 12px 2px 0; vertical-align: top; }
  .lines th { border-bottom: 1px solid #888; font-size: 10px; text-align: right; }
  .lines th:nth-child(-n+2) { text-align: left; }
  .lines td { border-bottom: 1px solid #e5e5e5; font-size: 10.5px; text-align: right; }
  .lines td:nth-child(-n+2) { text-align: left; }
  .totals { width: 45%; margin-left: auto; }
  .totals td { padding: 2px 4px; }
  .net td { font-weight: 800; border-top: 1px solid #888; }
  .sign { display: flex; justify-content: space-between; margin-top: 44px; }`;

function heading(letterhead: PurchaseLetterhead, title: string): string {
  return `
    <div class="hospital">${escapeHtml(letterhead.name)}</div>
    ${letterhead.address ? `<div class="small">${escapeHtml(letterhead.address)}</div>` : ''}
    <div class="small">${letterhead.gstin ? `GSTIN ${escapeHtml(letterhead.gstin)}` : ''}${letterhead.phone ? ` · Ph ${escapeHtml(letterhead.phone)}` : ''}</div>
    <h2 style="margin-top:10px">${escapeHtml(title)}</h2>`;
}

function taxColumns(interState: boolean): string {
  return interState ? '<th>IGST</th>' : '<th>CGST</th><th>SGST</th>';
}

function taxCells(interState: boolean, line: { cgstAmount: number; sgstAmount: number; igstAmount: number }): string {
  return interState ? `<td>${escapeHtml(formatInr(line.igstAmount))}</td>` : `<td>${escapeHtml(formatInr(line.cgstAmount))}</td><td>${escapeHtml(formatInr(line.sgstAmount))}</td>`;
}

function totalsTable(doc: { grossAmount: number; discountAmount: number; taxableAmount: number; cgstAmount: number; sgstAmount: number; igstAmount: number; roundOff: number; netAmount: number; isInterState: boolean }): string {
  return `<table class="totals">
    <tr><td>Gross</td><td class="right">${escapeHtml(formatInr(doc.grossAmount))}</td></tr>
    ${doc.discountAmount ? `<tr><td>Discount</td><td class="right">−${escapeHtml(formatInr(doc.discountAmount))}</td></tr>` : ''}
    <tr><td>Taxable value</td><td class="right">${escapeHtml(formatInr(doc.taxableAmount))}</td></tr>
    ${
      doc.isInterState
        ? `<tr><td>IGST</td><td class="right">${escapeHtml(formatInr(doc.igstAmount))}</td></tr>`
        : `<tr><td>CGST</td><td class="right">${escapeHtml(formatInr(doc.cgstAmount))}</td></tr><tr><td>SGST</td><td class="right">${escapeHtml(formatInr(doc.sgstAmount))}</td></tr>`
    }
    ${doc.roundOff ? `<tr><td>Round off</td><td class="right">${escapeHtml(formatInr(doc.roundOff))}</td></tr>` : ''}
    <tr class="net"><td>Total</td><td class="right">${escapeHtml(formatInr(doc.netAmount))}</td></tr>
  </table>`;
}

/** Purchase order for the vendor (A4), with GST per line as CGST + SGST or IGST exactly as saved. */
export function buildPurchaseOrderHtml(order: PurchaseOrderDetail, letterhead: PurchaseLetterhead): string {
  const rows = order.lines
    .map(
      (l, i) => `<tr><td>${i + 1}</td><td>${escapeHtml(l.itemName)}</td><td>${escapeHtml(formatQty(l.quantity, l.purchaseUnit))}</td><td>${l.freeQuantity ? escapeHtml(formatQty(l.freeQuantity)) : ''}</td>
        <td>${escapeHtml(formatInr(l.rate))}</td><td>${l.discountPercent ? escapeHtml(formatPercent(l.discountPercent)) : ''}</td><td>${escapeHtml(formatPercent(l.gstRate))}</td>
        <td>${escapeHtml(formatInr(l.taxableAmount))}</td>${taxCells(order.isInterState, l)}<td>${escapeHtml(formatInr(l.lineTotal))}</td></tr>`,
    )
    .join('');
  const body = `
    ${heading(letterhead, `Purchase order ${order.number}`)}
    <table class="meta">
      <tr><td>To</td><td><b>${escapeHtml(order.vendorName)}</b>${order.vendorGstin ? `<br/>GSTIN ${escapeHtml(order.vendorGstin)}` : ''}</td>
          <td>Order date</td><td>${escapeHtml(formatDateOnly(order.orderDate))}</td></tr>
      <tr><td>Deliver to</td><td>${escapeHtml(order.storeName)}</td><td>Expected by</td><td>${escapeHtml(formatDateOnly(order.expectedDeliveryDate))}</td></tr>
      <tr><td>Payment</td><td>${order.paymentTermsDays ? `${order.paymentTermsDays} days credit` : 'On delivery'}</td><td>Supply</td><td>${order.isInterState ? 'Inter-state (IGST)' : 'Within the state (CGST + SGST)'}</td></tr>
    </table>
    <div class="rule"></div>
    <table class="lines"><tr><th>#</th><th>Item</th><th>Qty</th><th>Free</th><th>Rate</th><th>Disc.</th><th>GST</th><th>Taxable</th>${taxColumns(order.isInterState)}<th>Amount</th></tr>${rows}</table>
    ${totalsTable(order)}
    ${order.remarks ? `<p>${escapeHtml(order.remarks)}</p>` : ''}
    <p class="small">Rates exclude GST. Please quote this order number on your invoice. Medicines must carry batch, expiry and MRP; goods expiring within 90 days may be refused.</p>
    <div class="sign"><div>Prepared by</div><div>Authorised signatory${order.approvedAt ? ` (approved ${escapeHtml(formatDateTime(order.approvedAt))})` : ''}</div></div>`;
  return printDocument(body, { title: `Purchase order ${order.number}`, pageSize: 'A4', styles: STYLES });
}

/** Goods receipt note (A4) as posted: batches, expiry, MRP, free goods and the GST split. */
export function buildGoodsReceiptHtml(grn: GoodsReceiptDetail, letterhead: PurchaseLetterhead): string {
  const rows = grn.lines
    .map(
      (l, i) => `<tr><td>${i + 1}</td><td>${escapeHtml(l.itemName)}<br/><span class="small">Batch ${escapeHtml(l.batchNumber)} · Exp ${escapeHtml(formatExpiry(l.expiryDate))} · MRP ${escapeHtml(formatInr(l.mrp))}</span></td>
        <td>${escapeHtml(formatQty(l.quantity))}</td><td>${l.freeQuantity ? escapeHtml(formatQty(l.freeQuantity)) : ''}</td><td>${escapeHtml(formatInr(l.rate))}</td>
        <td>${escapeHtml(formatPercent(l.gstRate))}</td><td>${escapeHtml(formatInr(l.taxableAmount))}</td>${taxCells(grn.isInterState, l)}<td>${escapeHtml(formatInr(l.lineTotal))}</td></tr>`,
    )
    .join('');
  const body = `
    ${heading(letterhead, `Goods receipt note ${grn.number}`)}
    <table class="meta">
      <tr><td>Vendor</td><td><b>${escapeHtml(grn.vendorName)}</b>${grn.vendorGstin ? `<br/>GSTIN ${escapeHtml(grn.vendorGstin)}` : ''}</td><td>Received on</td><td>${escapeHtml(formatDateOnly(grn.receivedOn))}</td></tr>
      <tr><td>Vendor invoice</td><td>${escapeHtml(grn.vendorInvoiceNumber)}${grn.vendorInvoiceDate ? ` dated ${escapeHtml(formatDateOnly(grn.vendorInvoiceDate))}` : ''}</td><td>Store</td><td>${escapeHtml(grn.storeName)}</td></tr>
      <tr><td>Status</td><td>${escapeHtml(grn.status === 'Posted' ? `Posted ${formatDateTime(grn.postedAt)}` : grn.status)}</td><td>Supply</td><td>${grn.isInterState ? 'Inter-state (IGST)' : 'Within the state (CGST + SGST)'}</td></tr>
    </table>
    <div class="rule"></div>
    <table class="lines"><tr><th>#</th><th>Item</th><th>Qty</th><th>Free</th><th>Rate</th><th>GST</th><th>Taxable</th>${taxColumns(grn.isInterState)}<th>Amount</th></tr>${rows}</table>
    ${totalsTable(grn)}
    ${grn.remarks ? `<p>${escapeHtml(grn.remarks)}</p>` : ''}
    <div class="sign"><div>Received by (store)</div><div>Checked by</div></div>`;
  return printDocument(body, { title: `GRN ${grn.number}`, pageSize: 'A4', styles: STYLES });
}

export async function printPurchaseOrder(order: PurchaseOrderDetail, letterhead: PurchaseLetterhead): Promise<void> {
  await printHtml(buildPurchaseOrderHtml(order, letterhead), { pageSize: 'A4' });
}

export async function printGoodsReceipt(grn: GoodsReceiptDetail, letterhead: PurchaseLetterhead): Promise<void> {
  await printHtml(buildGoodsReceiptHtml(grn, letterhead), { pageSize: 'A4' });
}
