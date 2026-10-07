import {
  escapeHtml,
  formatDateOnly,
  formatDateTime,
  formatInr,
  letterheadHtml,
  printDocument,
  printHtml,
  templateFor,
  type Letterhead,
} from '@hc360/core';
import { formatPercent } from '@/modules/inventory/format';
import { gstSummaryByRate } from '@/modules/pharmacy/print';
import {
  CUSTOMER_LABEL,
  hsnSacLabel,
  PAYMENT_LABEL,
  type CanteenSaleDto,
  type DailySalesSummary,
  type HsnSacSummary,
} from './api';
import { toPaise, toRupees } from './billCalculator';

const money = (value: number) => escapeHtml(formatInr(value).replace('₹', ''));
const percent = (rate: number) => escapeHtml(formatPercent(rate));

/**
 * The GST document a bill is (CGST Rules 46, 46A and 49): a tax invoice when every line is taxed, a bill of supply when
 * none is (nil-rated items such as tender coconut), and an invoice-cum-bill of supply when it has both.
 */
export function canteenBillTitle(lines: readonly { gstRate: number }[]): string {
  const taxed = lines.filter((l) => l.gstRate > 0).length;
  if (taxed === lines.length) {
    return 'Tax invoice';
  }
  return taxed === 0 ? 'Bill of supply' : 'Invoice-cum-bill of supply';
}

const RECEIPT_STYLES = `
  body { width: 72mm; }
  .small { font-size: 9px; }
  .title { font-weight: 800; margin-top: 4px; letter-spacing: 0.5px; }
  .dup { font-weight: 800; letter-spacing: 2px; margin-top: 2px; }
  td { padding: 2px 0; }
  .line { font-size: 9px; color: #333; }
  .net td { font-weight: 800; font-size: 13pt; border-top: 1px dashed #666; padding-top: 4px; }
  .gst th, .gst td { font-size: 9px; padding: 0 2px; text-align: right; }
  .gst th:first-child, .gst td:first-child { text-align: left; }
  .rule { border-top: 1px dashed #666; }
  .thanks { margin-top: 6px; }`;

/**
 * 80 mm thermal receipt, printed silently at the counter inside HC360 Desktop (the browser dialog elsewhere). A bill that
 * charges GST is a tax invoice (CGST Rules, rule 46): the supplier's name, address and GSTIN (left out when the facility
 * has none), the bill number and date, each line's HSN/SAC code and GST rate, the taxable value with CGST and SGST (also
 * by rate) and the total. It follows the pharmacy and canteen bills print template (logo, closing note).
 */
export function buildCanteenReceiptHtml(
  sale: CanteenSaleDto,
  letterhead: Letterhead,
  options: { duplicate?: boolean } = {},
): string {
  const rows = sale.lines
    .map((l) => {
      const details = [
        `${escapeHtml(l.quantity)} × ${money(l.unitPrice)}`,
        l.hsnSac ? escapeHtml(hsnSacLabel(l.hsnSac)) : null,
        `GST ${percent(l.gstRate)}`,
      ];
      return `<tr><td>${escapeHtml(l.itemName)}<div class="line">${details.filter(Boolean).join(' · ')}</div></td><td class="right">${money(l.lineTotal)}</td></tr>`;
    })
    .join('');
  const byRate = gstSummaryByRate(sale.lines)
    .map(
      (g) =>
        `<tr><td>${percent(g.rate)}</td><td>${money(g.taxable)}</td><td>${money(g.cgst)}</td><td>${money(g.sgst)}</td></tr>`,
    )
    .join('');
  const customer =
    sale.customerType === 'Staff' && sale.employeeCode
      ? `Staff ${escapeHtml(sale.employeeCode)}${sale.employeeName ? ` · ${escapeHtml(sale.employeeName)}` : ''}`
      : `${escapeHtml(CUSTOMER_LABEL[sale.customerType])}${sale.customerName ? ` · ${escapeHtml(sale.customerName)}` : ''}`;
  const template = templateFor(letterhead, 'sales-invoice');
  const body = `
    ${letterheadHtml(letterhead, template, { compact: true, taxIds: true })}
    <div class="center">
      <div class="title">${escapeHtml(canteenBillTitle(sale.lines))}</div>
      <div class="small">Canteen</div>
      ${options.duplicate ? '<div class="dup">DUPLICATE</div>' : ''}
      ${sale.status === 'Cancelled' ? '<div class="dup">CANCELLED</div>' : ''}
    </div>
    <div class="rule"></div>
    <div>Bill <strong>${escapeHtml(sale.billNumber)}</strong></div>
    <div class="muted">${escapeHtml(formatDateTime(sale.soldAt))}</div>
    <div class="muted">${customer}</div>
    <div class="rule"></div>
    <table>${rows}</table>
    <div class="rule"></div>
    <table>
      <tr><td>Taxable value</td><td class="right">${money(sale.taxableAmount)}</td></tr>
      <tr><td>CGST</td><td class="right">${money(sale.cgstAmount)}</td></tr>
      <tr><td>SGST</td><td class="right">${money(sale.sgstAmount)}</td></tr>
      ${sale.roundOff !== 0 ? `<tr><td>Round off</td><td class="right">${money(sale.roundOff)}</td></tr>` : ''}
      <tr class="net"><td>Total</td><td class="right">₹${money(sale.netAmount)}</td></tr>
    </table>
    ${byRate ? `<table class="gst"><tr><th>GST</th><th>Taxable</th><th>CGST</th><th>SGST</th></tr>${byRate}</table>` : ''}
    <div class="rule"></div>
    <div>Paid by ${escapeHtml(PAYMENT_LABEL[sale.paymentMode])}${sale.paymentReference ? ` · ${escapeHtml(sale.paymentReference)}` : ''}</div>
    ${sale.paymentMode === 'StaffCredit' ? '<div class="muted">Deducted from salary at month end.</div>' : ''}
    <div class="muted">Cashier: ${escapeHtml(sale.cashierName ?? '')}</div>
    <div class="center thanks">Thank you</div>`;
  return printDocument(body, {
    title: `Bill ${sale.billNumber}`,
    pageSize: 'Receipt80',
    styles: RECEIPT_STYLES,
    template,
  });
}

export async function printCanteenReceipt(
  sale: CanteenSaleDto,
  letterhead: Letterhead,
  options: { duplicate?: boolean } = {},
): Promise<void> {
  await printHtml(buildCanteenReceiptHtml(sale, letterhead, options), { pageSize: 'Receipt80', silent: true });
}

/** HSN/SAC rows with a total line; bills made before codes were recorded show as "Not recorded". */
function hsnSacTableHtml(rows: readonly HsnSacSummary[]): string {
  const sum = (pick: (h: HsnSacSummary) => number) =>
    money(toRupees(rows.reduce((total, h) => total + toPaise(pick(h)), 0)));
  const body = rows
    .map(
      (h) =>
        `<tr><td>${h.hsnSac ? escapeHtml(h.hsnSac) : 'Not recorded'}</td><td class="right">${percent(h.gstRate)}</td><td class="right">${escapeHtml(h.quantity)}</td><td class="right">${money(h.taxableAmount)}</td><td class="right">${money(h.cgst)}</td><td class="right">${money(h.sgst)}</td><td class="right">${money(h.total)}</td></tr>`,
    )
    .join('');
  return `<table><thead><tr><th>HSN/SAC</th><th class="right">GST</th><th class="right">Qty</th><th class="right">Taxable value</th><th class="right">CGST</th><th class="right">SGST</th><th class="right">Total</th></tr></thead>
    <tbody>${body}</tbody>
    <tfoot><tr class="total"><td colspan="3">Total</td><td class="right">${sum((h) => h.taxableAmount)}</td><td class="right">${sum((h) => h.cgst)}</td><td class="right">${sum((h) => h.sgst)}</td><td class="right">${sum((h) => h.total)}</td></tr></tfoot></table>`;
}

/** A4 day-end report (CAN-04): totals with the GST split, the HSN/SAC summary for GSTR-1, collections by mode and item-wise consumption. */
export function buildDailyReportHtml(report: DailySalesSummary, letterhead: Letterhead): string {
  const keyRows = (rows: { key: string; bills: number; amount: number }[], label: (key: string) => string) =>
    rows
      .map(
        (r) =>
          `<tr><td>${escapeHtml(label(r.key))}</td><td class="right">${escapeHtml(r.bills)}</td><td class="right">${money(r.amount)}</td></tr>`,
      )
      .join('');
  const items = report.items
    .map(
      (i) =>
        `<tr><td>${escapeHtml(i.itemName)}</td><td>${escapeHtml(i.category ?? '')}</td><td class="right">${escapeHtml(i.quantitySold)}</td><td class="right">${money(i.amount)}</td><td class="right">${escapeHtml(i.closingStock ?? '')}</td></tr>`,
    )
    .join('');
  const template = templateFor(letterhead, 'other');
  const body = `
    ${letterheadHtml(letterhead, template, { taxIds: true })}
    <h1>Canteen day-end report · ${escapeHtml(formatDateOnly(report.date))}</h1>
    <table class="summary">
      <tr><td>Bills</td><td class="right">${escapeHtml(report.bills)} (${escapeHtml(report.cancelledBills)} cancelled)</td></tr>
      <tr><td>Taxable value</td><td class="right">${money(report.taxableAmount)}</td></tr>
      <tr><td>CGST</td><td class="right">${money(report.cgst)}</td></tr>
      <tr><td>SGST</td><td class="right">${money(report.sgst)}</td></tr>
      <tr><td>Round off</td><td class="right">${money(report.roundOff)}</td></tr>
      <tr><td><strong>Net sales</strong></td><td class="right"><strong>₹${money(report.netSales)}</strong></td></tr>
    </table>
    <h2>HSN/SAC summary</h2>
    ${report.byHsnSac.length > 0 ? hsnSacTableHtml(report.byHsnSac) : '<p class="muted">No sales.</p>'}
    <h2>Collections by payment mode</h2>
    <table><thead><tr><th>Mode</th><th class="right">Bills</th><th class="right">Amount</th></tr></thead><tbody>${keyRows(report.byPaymentMode, (k) => PAYMENT_LABEL[k as keyof typeof PAYMENT_LABEL] ?? k)}</tbody></table>
    <h2>By customer</h2>
    <table><thead><tr><th>Customer</th><th class="right">Bills</th><th class="right">Amount</th></tr></thead><tbody>${keyRows(report.byCustomerType, (k) => CUSTOMER_LABEL[k as keyof typeof CUSTOMER_LABEL] ?? k)}</tbody></table>
    <h2>Items sold</h2>
    <table><thead><tr><th>Item</th><th>Category</th><th class="right">Qty</th><th class="right">Amount</th><th class="right">Closing stock</th></tr></thead><tbody>${items}</tbody></table>
    <div class="signs"><span>Cashier</span><span>Canteen manager</span></div>`;
  const styles = `
    h1 { font-size: 14pt; }
    h2 { font-size: 11pt; margin-top: 12px; }
    th { border-bottom: 1px solid #888; }
    td { border-bottom: 1px solid #eee; }
    .total td { font-weight: 700; border-top: 1px solid #888; }
    .summary { width: 60%; }
    .signs { display: flex; justify-content: space-between; margin-top: 36px; }
    .signs span { border-top: 1px solid #555; padding-top: 3px; min-width: 35%; text-align: center; }`;
  return printDocument(body, { title: `Canteen ${report.date}`, pageSize: 'A4', styles, template });
}

export async function printDailyReport(report: DailySalesSummary, letterhead: Letterhead): Promise<void> {
  await printHtml(buildDailyReportHtml(report, letterhead), { pageSize: 'A4' });
}
