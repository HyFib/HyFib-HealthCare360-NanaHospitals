import { escapeHtml, formatDateOnly, formatDateTime, formatInr, printDocument, printHtml } from '@hc360/core';
import { CUSTOMER_LABEL, PAYMENT_LABEL, type CanteenSaleDto, type DailySalesSummary } from './api';

const money = (value: number) => escapeHtml(formatInr(value).replace('₹', ''));

/**
 * 80 mm thermal receipt, printed silently at the counter inside HC360 Desktop (the browser dialog elsewhere). It is a
 * GST bill of supply: taxable value, CGST and SGST per the bill, round off and the net amount.
 */
export async function printCanteenReceipt(sale: CanteenSaleDto, facilityName: string, options: { duplicate?: boolean } = {}): Promise<void> {
  const rows = sale.lines
    .map(
      (l) => `<tr><td>${escapeHtml(l.itemName)}<div class="muted">${l.quantity} × ${money(l.unitPrice)} · GST ${l.gstRate}%</div></td><td class="right">${money(l.lineTotal)}</td></tr>`,
    )
    .join('');
  const customer =
    sale.customerType === 'Staff' && sale.employeeCode
      ? `Staff ${escapeHtml(sale.employeeCode)}${sale.employeeName ? ` · ${escapeHtml(sale.employeeName)}` : ''}`
      : `${escapeHtml(CUSTOMER_LABEL[sale.customerType])}${sale.customerName ? ` · ${escapeHtml(sale.customerName)}` : ''}`;
  const body = `
    <div class="center">
      <div class="hospital">${escapeHtml(facilityName)}</div>
      <div>Canteen</div>
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
    <div class="rule"></div>
    <div>Paid by ${escapeHtml(PAYMENT_LABEL[sale.paymentMode])}${sale.paymentReference ? ` · ${escapeHtml(sale.paymentReference)}` : ''}</div>
    ${sale.paymentMode === 'StaffCredit' ? '<div class="muted">Deducted from salary at month end.</div>' : ''}
    <div class="muted">Cashier: ${escapeHtml(sale.cashierName ?? '')}</div>
    <div class="center thanks">Thank you</div>`;
  const styles = `
    body { width: 72mm; }
    .hospital { font-weight: 700; font-size: 11pt; }
    .dup { font-weight: 800; letter-spacing: 2px; margin-top: 2px; }
    td { padding: 2px 0; }
    .net td { font-weight: 800; font-size: 13pt; border-top: 1px dashed #666; padding-top: 4px; }
    .rule { border-top: 1px dashed #666; }
    .thanks { margin-top: 6px; }`;
  await printHtml(printDocument(body, { title: `Bill ${sale.billNumber}`, pageSize: 'Receipt80', styles }), { pageSize: 'Receipt80', silent: true });
}

/** A4 day-end report (CAN-04): totals with the GST split, collections by mode and item-wise consumption. */
export async function printDailyReport(report: DailySalesSummary, facilityName: string): Promise<void> {
  const keyRows = (rows: { key: string; bills: number; amount: number }[], label: (key: string) => string) =>
    rows.map((r) => `<tr><td>${escapeHtml(label(r.key))}</td><td class="right">${r.bills}</td><td class="right">${money(r.amount)}</td></tr>`).join('');
  const items = report.items
    .map(
      (i) =>
        `<tr><td>${escapeHtml(i.itemName)}</td><td>${escapeHtml(i.category ?? '')}</td><td class="right">${i.quantitySold}</td><td class="right">${money(i.amount)}</td><td class="right">${i.closingStock ?? ''}</td></tr>`,
    )
    .join('');
  const body = `
    <div class="hospital">${escapeHtml(facilityName)} · Canteen</div>
    <h1>Day-end report · ${escapeHtml(formatDateOnly(report.date))}</h1>
    <table class="summary">
      <tr><td>Bills</td><td class="right">${report.bills} (${report.cancelledBills} cancelled)</td></tr>
      <tr><td>Taxable value</td><td class="right">${money(report.taxableAmount)}</td></tr>
      <tr><td>CGST</td><td class="right">${money(report.cgst)}</td></tr>
      <tr><td>SGST</td><td class="right">${money(report.sgst)}</td></tr>
      <tr><td>Round off</td><td class="right">${money(report.roundOff)}</td></tr>
      <tr><td><strong>Net sales</strong></td><td class="right"><strong>₹${money(report.netSales)}</strong></td></tr>
    </table>
    <h2>Collections by payment mode</h2>
    <table><thead><tr><th>Mode</th><th class="right">Bills</th><th class="right">Amount</th></tr></thead><tbody>${keyRows(report.byPaymentMode, (k) => PAYMENT_LABEL[k as keyof typeof PAYMENT_LABEL] ?? k)}</tbody></table>
    <h2>By customer</h2>
    <table><thead><tr><th>Customer</th><th class="right">Bills</th><th class="right">Amount</th></tr></thead><tbody>${keyRows(report.byCustomerType, (k) => CUSTOMER_LABEL[k as keyof typeof CUSTOMER_LABEL] ?? k)}</tbody></table>
    <h2>Items sold</h2>
    <table><thead><tr><th>Item</th><th>Category</th><th class="right">Qty</th><th class="right">Amount</th><th class="right">Closing stock</th></tr></thead><tbody>${items}</tbody></table>
    <div class="signs"><span>Cashier</span><span>Canteen manager</span></div>`;
  const styles = `
    .hospital { font-weight: 700; color: #045E80; }
    h1 { font-size: 14pt; }
    h2 { font-size: 11pt; margin-top: 12px; }
    th { border-bottom: 1px solid #888; }
    td { border-bottom: 1px solid #eee; }
    .summary { width: 60%; }
    .signs { display: flex; justify-content: space-between; margin-top: 36px; }
    .signs span { border-top: 1px solid #555; padding-top: 3px; min-width: 35%; text-align: center; }`;
  await printHtml(printDocument(body, { title: `Canteen ${report.date}`, pageSize: 'A4', styles }), { pageSize: 'A4' });
}
