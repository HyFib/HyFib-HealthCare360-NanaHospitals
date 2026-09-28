import { escapeHtml } from '@hc360/core';
import type { Translate } from '@/i18n';
import type { Formatters } from '@/i18n/useFormat';
import { safeFileName, type PrintableDocument } from '@/lib/print';
import type { PortalBillDetail, ReceiptSummary } from '@/lib/types';
import { BILL_STATUS, BILL_TYPE, RECEIPT_PURPOSE } from './labels';

export interface BillPrintContext {
  t: Translate;
  f: Formatters;
  hospital: string;
  uhid: string;
  printedAt: string;
}

const STYLES = `
.head { display: flex; justify-content: space-between; gap: 12px; border-bottom: 2px solid #0A7CA7; padding-bottom: 6px; margin-bottom: 8px; }
.facility { font-size: 14pt; font-weight: 700; color: #045E80; }
.totals td { border: none; padding: 2px 6px; }
.totals .grand td { font-weight: 700; border-top: 1px solid #999; }
.receipt { max-width: 120mm; }
.amount { font-size: 18pt; font-weight: 700; }
.footer { margin-top: 16px; font-size: 8.5pt; color: #555; border-top: 1px solid #ccc; padding-top: 4px; }
`;

function head(title: string, subtitle: string, ctx: BillPrintContext): string {
  return `<div class="head"><div><div class="facility">${escapeHtml(ctx.hospital)}</div></div><div class="right"><h1>${escapeHtml(title)}</h1><div class="small">${escapeHtml(
    subtitle,
  )}</div></div></div>`;
}

function footer(ctx: BillPrintContext, note?: string): string {
  return `<div class="footer">${note ? `${escapeHtml(note)}<br/>` : ''}${escapeHtml(ctx.t('print.printedOn', { date: ctx.f.dateTime(ctx.printedAt) }))}</div>`;
}

/** An itemised copy of a bill with its totals and payments. */
export function billDocument(bill: PortalBillDetail, ctx: BillPrintContext): PrintableDocument {
  const { t, f } = ctx;
  const number = bill.billNumber ?? '';
  const rows = bill.lines
    .map(
      (line) =>
        `<tr class="avoid-break"><td>${escapeHtml(line.description)}${line.isReturn ? ` <span class="small">(${escapeHtml(t('bills.returned'))})</span>` : ''}<div class="small muted">${escapeHtml(
          f.date(line.serviceDate),
        )}</div></td><td class="right">${escapeHtml(f.number(line.quantity))}</td><td class="right">${escapeHtml(f.inr(line.unitPrice))}</td><td class="right">${escapeHtml(
          line.discountAmount ? f.inr(line.discountAmount) : '',
        )}</td><td class="right">${escapeHtml(line.taxAmount ? f.inr(line.taxAmount) : '')}</td><td class="right">${escapeHtml(f.inr(line.netAmount))}</td></tr>`,
    )
    .join('');
  const total = (label: string, value: number, grand = false) =>
    `<tr${grand ? ' class="grand"' : ''}><td>${escapeHtml(label)}</td><td class="right">${escapeHtml(f.inr(value))}</td></tr>`;
  const totals = [
    total(t('bills.gross'), bill.grossAmount),
    bill.discountAmount ? total(t('bills.discounts'), -bill.discountAmount) : '',
    bill.taxAmount ? total(t('bills.gst'), bill.taxAmount) : '',
    bill.roundOffAmount ? total(t('bills.roundOff'), bill.roundOffAmount) : '',
    total(t('bills.net'), bill.netAmount, true),
    bill.payerAmount ? total(t('bills.payer'), bill.payerAmount) : '',
    total(t('bills.patientShare'), bill.patientAmount),
    total(t('bills.paid'), bill.paidAmount),
    total(t('bills.due'), bill.dueAmount, true),
  ].join('');
  const receipts = bill.receipts.length
    ? `<h2>${escapeHtml(t('bills.receipts'))}</h2><table><tbody>${bill.receipts
        .map(
          (r) =>
            `<tr><td>${escapeHtml(t('bills.receipt', { number: r.receiptNumber }))}</td><td>${escapeHtml(f.dateTime(r.receivedAt))}</td><td>${escapeHtml(
              r.modes,
            )}</td><td class="right">${escapeHtml(f.inr(r.amount))}</td></tr>`,
        )
        .join('')}</tbody></table>`
    : '';
  const body = `${head(t('bills.print.title'), `${t('bills.number', { number })} · ${f.date(bill.billedAt)}`, ctx)}
<div class="patient"><strong>${escapeHtml(bill.patientName ?? '')}</strong> · ${escapeHtml(t('common.uhid'))} ${escapeHtml(ctx.uhid)} · ${escapeHtml(
    t(BILL_TYPE[bill.type]),
  )} · ${escapeHtml(t(BILL_STATUS[bill.status].key))}</div>
<h2>${escapeHtml(t('bills.items'))}</h2>
<table><thead><tr><th></th><th class="right">#</th><th class="right">₹</th><th class="right">${escapeHtml(t('bills.discounts'))}</th><th class="right">${escapeHtml(
    t('bills.gst'),
  )}</th><th class="right">${escapeHtml(t('common.total'))}</th></tr></thead><tbody>${rows}</tbody></table>
<h2>${escapeHtml(t('bills.summary'))}</h2><table class="totals"><tbody>${totals}</tbody></table>${receipts}${footer(ctx, t('bills.help'))}`;
  return { title: `${t('bills.print.title')} ${number}`, body, styles: STYLES, fileName: safeFileName(`bill-${number}`) };
}

/** A copy of one payment receipt. */
export function receiptDocument(receipt: ReceiptSummary, bill: PortalBillDetail, ctx: BillPrintContext): PrintableDocument {
  const { t, f } = ctx;
  const body = `<div class="receipt">${head(t('bills.receipt.title'), t('bills.receipt', { number: receipt.receiptNumber }), ctx)}
<p class="amount">${escapeHtml(f.inr(receipt.amount))}</p>
<table><tbody>
<tr><td class="muted">${escapeHtml(t('print.patient'))}</td><td>${escapeHtml(bill.patientName ?? '')} (${escapeHtml(t('common.uhid'))} ${escapeHtml(ctx.uhid)})</td></tr>
<tr><td class="muted">${escapeHtml(t('common.date'))}</td><td>${escapeHtml(f.dateTime(receipt.receivedAt))}</td></tr>
<tr><td class="muted">${escapeHtml(t('bills.receipt.mode'))}</td><td>${escapeHtml(receipt.modes)}</td></tr>
<tr><td class="muted">${escapeHtml(t('common.details'))}</td><td>${escapeHtml(t(RECEIPT_PURPOSE[receipt.purpose]))} · ${escapeHtml(
    t('bills.receipt.for', { number: bill.billNumber ?? '' }),
  )}</td></tr>
</tbody></table>${footer(ctx, t('bills.receipt.note'))}</div>`;
  return { title: `${t('bills.receipt.title')} ${receipt.receiptNumber}`, body, styles: STYLES, fileName: safeFileName(`receipt-${receipt.receiptNumber}`) };
}
