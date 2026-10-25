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
  type PrintTemplateKey,
} from '@hc360/core';
import { gstStateName } from '../inventory/gst';
import type { BillDetail, DayEndReport, ReceiptDto, ReceiptPaymentDto, RefundDto } from './api';
import type { RunningBill } from './ipApi';
import {
  PAYMENT_MODE_LABEL,
  RECEIPT_PURPOSE_LABEL,
  REFUND_PURPOSE_LABEL,
  SERVICE_CATEGORY_LABEL,
  modeSummaryLabel,
} from './labels';
import { amountInWords, formatPercent, fromPaise, toPaise } from './money';

/**
 * Receipt, counter bill and report prints. Everything is built as a self-contained HTML string (every value escaped)
 * and sent to `printHtml`: counters print 80 mm receipts silently on the thermal printer, other prints open the print
 * dialog. The A4 bill (tax invoice / bill of supply with GST details) is made by the server instead, so the printed
 * copy matches its PDF (see `@/lib/documents`). Each print follows its print template (`templateFor`): the letterhead
 * or pre-printed stationery, the logo and the closing note.
 */

export type PrintFormat = 'A4' | 'Receipt80';

const e = escapeHtml;
const money = (value: number | null | undefined) => e(formatInr(value));

interface HeaderOptions {
  compact: boolean;
  /** GSTIN, PAN and the registration number, which bills, receipts and vouchers show. */
  ids?: boolean;
}

/** The letterhead with the document's title and numbers: centred on an 80 mm roll, on the right of an A4 page. */
function header(
  letterhead: Letterhead,
  key: PrintTemplateKey,
  title: string,
  meta: [string, string][],
  { compact, ids = true }: HeaderOptions,
): string {
  const template = templateFor(letterhead, key);
  if (compact) {
    return `${letterheadHtml(letterhead, template, { compact: true, taxIds: ids })}
    <div class="rule"></div>
    <div class="center h-title">${e(title)}</div>
    <table class="meta">${meta.map(([k, v]) => `<tr><td>${e(k)}</td><td class="right">${e(v)}</td></tr>`).join('')}</table>`;
  }
  const aside = `<div class="h-title">${e(title)}</div>${meta.map(([k, v]) => `<div>${e(k)}: <b>${e(v)}</b></div>`).join('')}`;
  return letterheadHtml(letterhead, template, { taxIds: ids, registration: ids, aside });
}

const A4_STYLES = `
  .h-title { font-size: 16px; font-weight: 700; text-transform: uppercase; letter-spacing: .5px; }
  .box { border: 1px solid #bbb; border-radius: 4px; padding: 6px 8px; margin: 8px 0; }
  .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 2px 16px; }
  table.lines { margin-top: 6px; }
  table.lines th { border-bottom: 1px solid #555; font-size: 10.5px; background: #f2f2f2; }
  table.lines td { border-bottom: 1px solid #e3e3e3; font-size: 10.5px; }
  .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
  .totals { width: 55%; margin-left: auto; margin-top: 8px; }
  .totals td { padding: 2px 4px; }
  .totals .strong td { font-weight: 700; border-top: 1px solid #555; font-size: 13px; }
  .words { margin-top: 6px; font-style: italic; }
  .sign { margin-top: 36px; display: flex; justify-content: space-between; }
  .sign div { border-top: 1px solid #555; padding-top: 2px; min-width: 180px; text-align: center; }
  .foot { margin-top: 12px; font-size: 9.5px; color: #555; }
  h3 { margin-top: 10px; font-size: 12px; }`;

const RECEIPT80_STYLES = `
  body { width: 72mm; }
  .h-title { font-size: 12px; font-weight: 700; text-transform: uppercase; margin: 2px 0; }
  .small { font-size: 9.5px; }
  .meta td, .items td, .sum td { padding: 1px 0; }
  .items .desc { font-weight: 600; }
  .num { text-align: right; white-space: nowrap; }
  .sum .strong td { font-weight: 800; font-size: 13px; border-top: 1px dashed #333; }
  .rule { border-top: 1px dashed #333; margin: 4px 0; }
  .words { font-size: 9.5px; font-style: italic; }
  .stamp { text-align: center; font-weight: 800; font-size: 14px; border: 2px solid #000; margin: 4px 0; }`;

function watermark(bill: Pick<BillDetail, 'status'>): string {
  if (bill.status === 'Cancelled' || bill.status === 'Refunded') {
    return 'CANCELLED';
  }
  return bill.status === 'Draft' ? 'DRAFT' : '';
}

/** Tax invoice when any line carries GST; otherwise a bill of supply (most healthcare services are exempt). */
export function billTitle(bill: Pick<BillDetail, 'isTaxInvoice' | 'status' | 'type' | 'isFinalBill'>): string {
  if (bill.status === 'Draft') {
    return 'Estimate (draft bill)';
  }
  const kind = bill.isTaxInvoice ? 'Tax invoice' : 'Bill of supply';
  if (bill.type === 'InPatient') {
    return `${kind} · ${bill.isFinalBill ? 'IP final bill' : 'IP bill'}`;
  }
  return kind;
}

function totalsRows(bill: BillDetail): [string, number, boolean?][] {
  const rows: [string, number, boolean?][] = [['Gross amount', bill.grossAmount]];
  if (bill.packageCoveredAmount !== 0) {
    rows.push(['Less: covered by package', -bill.packageCoveredAmount]);
  }
  if (bill.tariffDiscountAmount !== 0) {
    rows.push(['Less: tariff concession', -bill.tariffDiscountAmount]);
  }
  const staff = fromPaise(toPaise(bill.lineDiscountAmount) + toPaise(bill.billDiscountAmount));
  if (staff !== 0) {
    rows.push([`Less: discount${bill.discountReason ? ` (${bill.discountReason})` : ''}`, -staff]);
  }
  if (bill.isTaxInvoice) {
    rows.push(['Taxable value', bill.taxableAmount]);
    if (bill.igstAmount !== 0) {
      rows.push(['IGST', bill.igstAmount]);
    } else {
      rows.push(['CGST', bill.cgstAmount], ['SGST', bill.sgstAmount]);
    }
  }
  if (bill.roundOffAmount !== 0) {
    rows.push(['Round off', bill.roundOffAmount]);
  }
  rows.push(['Net amount', bill.netAmount, true]);
  if (bill.payerAmount !== 0) {
    rows.push(
      [`Payable by ${bill.payerName ?? 'insurer'}`, bill.payerAmount],
      ['Payable by patient', bill.patientAmount, true],
    );
  }
  if (bill.creditAmount !== 0) {
    rows.push(['Credit notes / write-off', -bill.creditAmount]);
  }
  if (bill.status !== 'Draft') {
    rows.push(['Paid', bill.paidAmount]);
    if (bill.refundedAmount !== 0) {
      rows.push(['Refunded', -bill.refundedAmount]);
    }
    rows.push(
      bill.refundableAmount > 0
        ? ['Refundable to patient', bill.refundableAmount, true]
        : ['Balance due', bill.dueAmount, true],
    );
  }
  return rows;
}

function billMeta(bill: BillDetail): [string, string][] {
  const meta: [string, string][] = [
    ['Bill no.', bill.billNumber ?? 'Draft'],
    ['Date', formatDateTime(bill.finalizedAt ?? bill.createdAt)],
  ];
  const supply = bill.isTaxInvoice ? gstStateName(bill.placeOfSupply) : null;
  if (supply) {
    meta.push(['Place of supply', supply]);
  }
  return meta;
}

/** Compact bill for the counter's 80 mm receipt printer (the A4 bill is made by the server). */
export function buildCounterBillHtml(bill: BillDetail, letterhead: Letterhead): string {
  const title = billTitle(bill);
  const stamp = watermark(bill);
  const patient = `${bill.patientName ?? ''}${bill.uhid ? ` (UHID ${bill.uhid})` : ''}`;
  const items = bill.lines
    .map(
      (line) => `<tr><td colspan="2" class="desc">${e(line.description)}${line.isReturn ? ' (return)' : ''}</td></tr>
        <tr><td class="small">${e(line.quantity)} × ${money(line.unitPrice)}${line.taxRate > 0 ? ` · GST ${e(formatPercent(line.taxRate))}` : ''}${line.sacCode ? ` · SAC ${e(line.sacCode)}` : ''}</td><td class="num">${money(line.netAmount)}</td></tr>`,
    )
    .join('');
  const sums = totalsRows(bill)
    .map(
      ([label, value, strong]) =>
        `<tr class="${strong ? 'strong' : ''}"><td>${e(label)}</td><td class="num">${money(value)}</td></tr>`,
    )
    .join('');
  const body = `${stamp ? `<div class="stamp">${e(stamp)}</div>` : ''}
      ${header(letterhead, 'bill', title, [...billMeta(bill), ['Patient', patient]], { compact: true })}
      <div class="rule"></div>
      <table class="items">${items}</table>
      <div class="rule"></div>
      <table class="sum">${sums}</table>
      <div class="words">${e(amountInWords(bill.patientAmount))}</div>
      ${bill.receipts.length ? `<div class="rule"></div>${bill.receipts.map((r) => `<div class="small">${e(r.receiptNumber)} · ${e(modeSummaryLabel(r.modes))} · ${money(r.amount)}</div>`).join('')}` : ''}
      <div class="rule"></div>
      <div class="center small">Thank you. Get well soon.</div>`;
  return printDocument(body, {
    title: `${title} ${bill.billNumber ?? ''}`.trim(),
    pageSize: 'Receipt80',
    styles: RECEIPT80_STYLES,
    template: templateFor(letterhead, 'bill'),
  });
}

function paymentDetail(payment: ReceiptPaymentDto): string {
  switch (payment.mode) {
    case 'Card':
      return [
        payment.cardNetwork,
        payment.cardLast4 ? `ending ${payment.cardLast4}` : '',
        payment.reference ? `ref ${payment.reference}` : '',
      ]
        .filter(Boolean)
        .join(' ');
    case 'Cheque':
      return [
        payment.chequeNumber ? `No. ${payment.chequeNumber}` : '',
        payment.chequeBank ?? '',
        payment.chequeDate ? `dated ${formatDateOnly(payment.chequeDate)}` : '',
      ]
        .filter(Boolean)
        .join(', ');
    default:
      return payment.reference ? `Ref ${payment.reference}` : '';
  }
}

function receiptTitle(receipt: ReceiptDto): string {
  switch (receipt.purpose) {
    case 'Deposit':
      return 'Advance receipt (in-patient)';
    case 'Advance':
      return 'Advance receipt';
    case 'PayerSettlement':
      return 'Settlement receipt';
    default:
      return 'Payment receipt';
  }
}

/** Payment or advance receipt. */
export function buildReceiptHtml(receipt: ReceiptDto, letterhead: Letterhead, format: PrintFormat): string {
  const title = receiptTitle(receipt);
  const template = templateFor(letterhead, 'receipt');
  const meta: [string, string][] = [
    ['Receipt no.', receipt.receiptNumber],
    ['Date', formatDateTime(receipt.receivedAt)],
  ];
  if (receipt.billNumber) {
    meta.push(['Against bill', receipt.billNumber]);
  }
  const patient = `${receipt.patientName ?? ''}${receipt.uhid ? ` (UHID ${receipt.uhid})` : ''}`;

  if (format === 'Receipt80') {
    const lines = receipt.payments
      .map((p) => {
        const detail = paymentDetail(p);
        return `<tr><td>${e(PAYMENT_MODE_LABEL[p.mode])}${detail ? `<div class="small">${e(detail)}</div>` : ''}</td><td class="num">${money(p.amount)}</td></tr>`;
      })
      .join('');
    const body = `${header(letterhead, 'receipt', title, [...meta, ['Patient', patient]], { compact: true })}
      <div class="rule"></div>
      <table class="sum">${lines}<tr class="strong"><td>Total received</td><td class="num">${money(receipt.amount)}</td></tr></table>
      <div class="words">${e(amountInWords(receipt.amount))}</div>
      <div class="rule"></div>
      <div class="small">Received by ${e(receipt.receivedByName ?? '—')}${receipt.counter ? ` at ${e(receipt.counter)}` : ''}</div>
      ${receipt.notes ? `<div class="small">${e(receipt.notes)}</div>` : ''}
      <div class="center small">Please keep this receipt for refunds and claims.</div>`;
    return printDocument(body, {
      title: `${title} ${receipt.receiptNumber}`,
      pageSize: 'Receipt80',
      styles: RECEIPT80_STYLES,
      template,
    });
  }

  const rows = receipt.payments
    .map(
      (p) =>
        `<tr><td>${e(PAYMENT_MODE_LABEL[p.mode])}</td><td>${e(paymentDetail(p) || '—')}</td><td class="num">${money(p.amount)}</td></tr>`,
    )
    .join('');
  const body = `${header(letterhead, 'receipt', title, meta, { compact: false })}
    <div class="box grid">
      <div>Received from: <b>${e(receipt.patientName ?? '—')}</b></div>
      <div>UHID: <b>${e(receipt.uhid ?? '—')}</b></div>
      <div>Towards: ${e(RECEIPT_PURPOSE_LABEL[receipt.purpose])}${receipt.billNumber ? ` (bill ${e(receipt.billNumber)})` : ''}</div>
    </div>
    <table class="lines"><tr><th>Mode</th><th>Details</th><th class="num">Amount</th></tr>${rows}</table>
    <table class="totals"><tr class="strong"><td>Total received</td><td class="num">${money(receipt.amount)}</td></tr></table>
    <div class="words">${e(amountInWords(receipt.amount))}</div>
    ${receipt.notes ? `<div class="foot">${e(receipt.notes)}</div>` : ''}
    <div class="sign"><div>Received by ${e(receipt.receivedByName ?? '')}${receipt.counter ? `, ${e(receipt.counter)}` : ''}</div><div>Authorised signatory</div></div>
    <div class="foot">This is a computer-generated receipt. Cheque payments are subject to realisation.</div>`;
  return printDocument(body, {
    title: `${title} ${receipt.receiptNumber}`,
    pageSize: 'A4',
    styles: A4_STYLES,
    template,
  });
}

/** Day-end collection report (A4) for the accounts office. */
export function buildDayEndHtml(
  report: DayEndReport,
  letterhead: Letterhead,
  printedBy: string,
  printedAt: string,
): string {
  const meta: [string, string][] = [
    ['Date', formatDateOnly(report.date)],
    ['Counter', report.counter ?? 'All counters'],
  ];
  const byMode = report.byMode
    .map(
      (m) =>
        `<tr><td>${e(PAYMENT_MODE_LABEL[m.mode])}</td><td class="num">${money(m.collected)}</td><td class="num">${money(m.refunded)}</td><td class="num">${money(m.net)}</td></tr>`,
    )
    .join('');
  const shifts = report.shifts
    .map(
      (
        s,
      ) => `<tr><td>${e(s.userName)}</td><td>${e(s.counter)}</td><td>${e(formatDateTime(s.openedAt))}</td><td>${e(s.closedAt ? formatDateTime(s.closedAt) : 'Still open')}</td>
      <td class="num">${money(s.collected)}</td><td class="num">${money(s.refunded)}</td><td class="num">${money(s.expectedCash)}</td><td class="num">${money(s.declaredCash)}</td><td class="num">${money(s.variance)}</td></tr>`,
    )
    .join('');
  const body = `${header(letterhead, 'other', 'Day-end collection report', meta, { compact: false, ids: false })}
    ${report.openShifts > 0 ? `<div class="box"><b>${e(report.openShifts)} shift(s) still open.</b> Their figures can still change.</div>` : ''}
    <table class="totals" style="width:100%">
      <tr><td>Total collected</td><td class="num">${money(report.totalCollected)}</td><td>Bills finalised</td><td class="num">${e(report.billsFinalized)} (${money(report.billedAmount)})</td></tr>
      <tr><td>Total refunded</td><td class="num">${money(report.totalRefunded)}</td><td>Discounts given</td><td class="num">${money(report.discountGiven)}</td></tr>
      <tr class="strong"><td>Net collection</td><td class="num">${money(report.netCollection)}</td><td>Bills cancelled</td><td class="num">${e(report.billsCancelled)} (${money(report.cancelledAmount)})</td></tr>
      <tr><td>IP advances collected</td><td class="num">${money(report.depositsCollected)}</td><td></td><td></td></tr>
    </table>
    <h3>By payment mode</h3>
    <table class="lines"><tr><th>Mode</th><th class="num">Collected</th><th class="num">Refunded</th><th class="num">Net</th></tr>${byMode || '<tr><td colspan="4">No collections.</td></tr>'}</table>
    <h3>By cashier</h3>
    <table class="lines"><tr><th>Cashier</th><th>Counter</th><th>Opened</th><th>Closed</th><th class="num">Collected</th><th class="num">Refunded</th><th class="num">Expected cash</th><th class="num">Declared cash</th><th class="num">Short / excess</th></tr>${shifts || '<tr><td colspan="9">No shifts.</td></tr>'}</table>
    <div class="sign"><div>Cashier</div><div>Accounts</div></div>
    <div class="foot">Printed by ${e(printedBy)} on ${e(formatDateTime(printedAt))}. A negative short/excess is a cash shortage.</div>`;
  return printDocument(body, {
    title: `Day-end report ${report.date}`,
    pageSize: 'A4',
    styles: A4_STYLES,
    template: templateFor(letterhead, 'other'),
  });
}

/** Refund voucher signed by the patient when money is handed back. */
export function buildRefundVoucherHtml(refund: RefundDto, letterhead: Letterhead): string {
  const meta: [string, string][] = [
    ['Voucher no.', refund.refundNumber],
    ['Date', formatDateTime(refund.paidAt ?? refund.requestedAt)],
  ];
  const body = `${header(letterhead, 'receipt', 'Refund voucher', meta, { compact: false })}
    <div class="box grid">
      <div>Paid to: <b>${e(refund.patientName ?? '—')}</b></div>
      <div>UHID: <b>${e(refund.uhid ?? '—')}</b></div>
      <div>Towards: ${e(REFUND_PURPOSE_LABEL[refund.purpose])}</div>
      <div>Mode: ${e(refund.mode ? PAYMENT_MODE_LABEL[refund.mode] : 'Not paid yet')}</div>
      ${refund.reference ? `<div>Reference: ${e(refund.reference)}</div>` : ''}
    </div>
    <div>Reason: ${e(refund.reason)}</div>
    <table class="totals"><tr class="strong"><td>Amount refunded</td><td class="num">${money(refund.amount)}</td></tr></table>
    <div class="words">${e(amountInWords(refund.amount))}</div>
    <div class="foot">Requested by ${e(refund.requestedByName ?? 'the system')} on ${e(formatDateTime(refund.requestedAt))}${refund.decidedAt ? `; approved on ${e(formatDateTime(refund.decidedAt))}` : ''}.</div>
    <div class="sign"><div>Received by (patient / attendant)</div><div>Cashier</div></div>`;
  return printDocument(body, {
    title: `Refund ${refund.refundNumber}`,
    pageSize: 'A4',
    styles: A4_STYLES,
    template: templateFor(letterhead, 'receipt'),
  });
}

/** In-patient running bill: an interim statement for the patient's family (not a bill). */
export function buildRunningBillHtml(
  bill: RunningBill,
  letterhead: Letterhead,
  patient: { name: string; uhid: string | null },
  printedAt: string,
): string {
  const meta: [string, string][] = [
    ['Admission', bill.admissionNumber],
    ['As on', formatDateTime(printedAt)],
  ];
  const categories = bill.byCategory
    .map(
      (c) =>
        `<tr><td>${e(SERVICE_CATEGORY_LABEL[c.category])}</td><td class="num">${e(c.items)}</td><td class="num">${money(c.gross)}</td><td class="num">${money(c.discount)}</td><td class="num">${money(c.tax)}</td><td class="num">${money(c.net)}</td></tr>`,
    )
    .join('');
  const days = bill.byDate
    .map(
      (d) =>
        `<tr><td>${e(formatDateOnly(d.date))}</td><td class="num">${e(d.items)}</td><td class="num">${money(d.net)}</td></tr>`,
    )
    .join('');
  const body = `${header(letterhead, 'bill', 'Running bill (interim statement)', meta, { compact: false })}
    <div class="box grid"><div>Patient: <b>${e(patient.name)}</b></div><div>UHID: <b>${e(patient.uhid ?? '—')}</b></div></div>
    <h3>By category</h3>
    <table class="lines"><tr><th>Category</th><th class="num">Items</th><th class="num">Amount</th><th class="num">Discount</th><th class="num">GST</th><th class="num">Net</th></tr>${categories}</table>
    <h3>By day</h3>
    <table class="lines"><tr><th>Date</th><th class="num">Items</th><th class="num">Net</th></tr>${days}</table>
    <table class="totals">
      <tr><td>Services so far</td><td class="num">${money(bill.netTotal)}</td></tr>
      ${bill.returnsNet !== 0 ? `<tr><td>Of which returns</td><td class="num">${money(bill.returnsNet)}</td></tr>` : ''}
      ${bill.insurance ? `<tr><td>Estimated insurer share</td><td class="num">${money(bill.insurance.payerShare)}</td></tr>` : ''}
      <tr><td>Estimated payable by patient</td><td class="num">${money(bill.estimatedPatientPayable)}</td></tr>
      <tr><td>Paid on bills</td><td class="num">${money(bill.paidOnBills)}</td></tr>
      <tr><td>Advance balance</td><td class="num">${money(bill.depositBalance)}</td></tr>
      <tr class="strong"><td>${bill.estimatedBalance >= 0 ? 'Estimated balance to pay' : 'Estimated refund'}</td><td class="num">${money(Math.abs(bill.estimatedBalance))}</td></tr>
    </table>
    <div class="foot">This statement is an estimate while the patient is in hospital. The final bill may differ (package, insurer approval, pending services).</div>`;
  return printDocument(body, {
    title: `Running bill ${bill.admissionNumber}`,
    pageSize: 'A4',
    styles: A4_STYLES,
    template: templateFor(letterhead, 'bill'),
  });
}

/** Sends a document to the printer: 80 mm prints go silently to the counter's receipt printer. */
export async function sendToPrinter(
  html: string,
  format: PrintFormat,
  options: { silent?: boolean; copies?: number } = {},
): Promise<void> {
  await printHtml(html, {
    pageSize: format,
    silent: format === 'Receipt80' ? (options.silent ?? true) : false,
    copies: options.copies,
  });
}
