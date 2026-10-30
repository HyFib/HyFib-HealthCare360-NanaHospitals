import {
  escapeHtml,
  formatDateOnly,
  formatDateTime,
  formatInr,
  letterheadHtml,
  printDocument,
  printHtml,
  templateFor,
  type DispensingLabel,
  type Letterhead,
} from '@hc360/core';
import { printLabelJob } from '@/lib/labelPrinting';
import { dec, sum, toNumber } from '@/modules/inventory/decimal';
import { formatExpiry, formatPercent, formatQty } from '@/modules/inventory/format';
import { gstStateName } from '@/modules/inventory/gst';
import type { IpIssueDetail, PharmacySaleDetail } from './api';
import { paymentModeLabel } from './api';

export interface GstRateSummary {
  rate: number;
  taxable: number;
  cgst: number;
  sgst: number;
  igst: number;
}

/** GST summary by rate from the invoice lines exactly as the server priced them (no recalculation). */
export function gstSummaryByRate(
  items: readonly {
    gstRate: number;
    taxableAmount: number;
    cgstAmount: number;
    sgstAmount: number;
    igstAmount?: number;
  }[],
): GstRateSummary[] {
  const rates = Array.from(new Set(items.map((i) => i.gstRate))).sort((a, b) => a - b);
  return rates.map((rate) => {
    const lines = items.filter((i) => i.gstRate === rate);
    const add = (pick: (i: (typeof lines)[number]) => number) => toNumber(sum(lines.map((i) => dec(pick(i)))));
    return {
      rate,
      taxable: add((i) => i.taxableAmount),
      cgst: add((i) => i.cgstAmount),
      sgst: add((i) => i.sgstAmount),
      igst: add((i) => i.igstAmount ?? 0),
    };
  });
}

const RECEIPT_STYLES = `
  .receipt { width: 72mm; }
  .title { font-weight: 700; margin: 4px 0 2px; letter-spacing: 0.5px; }
  .small { font-size: 9px; }
  .muted { color: #444; }
  .dash { border-top: 1px dashed #333; margin: 4px 0; }
  .line td { padding: 1px 0; }
  .item { font-weight: 600; }
  .sub td { font-size: 9px; color: #333; padding: 0 0 3px; }
  .totals td { padding: 1px 0; }
  .net td { font-weight: 800; font-size: 13px; padding-top: 3px; }
  .gst th, .gst td { font-size: 9px; padding: 0 2px; text-align: right; }
  .gst th:first-child, .gst td:first-child { text-align: left; }
  .cancelled { border: 2px solid #b00; color: #b00; font-weight: 800; text-align: center; margin: 4px 0; }`;

/** 80 mm GST invoice / cash memo for a pharmacy sale (OP dispensing or counter sale), with the sales-invoice template. */
export function buildReceiptHtml(
  sale: PharmacySaleDetail,
  letterhead: Letterhead,
  options: { copyLabel?: string } = {},
): string {
  const template = templateFor(letterhead, 'sales-invoice');
  const customer = sale.buyerLegalName
    ? escapeHtml(sale.buyerLegalName)
    : sale.customerName
      ? `${escapeHtml(sale.customerName)}${sale.patientUhid ? ` (${escapeHtml(sale.patientUhid)})` : ''}`
      : 'Walk-in customer';
  const rows = sale.items
    .map(
      (item) => `
        <tr class="line"><td class="item" colspan="2">${escapeHtml(item.itemName)}${item.isSubstitution ? ' *' : ''}</td></tr>
        <tr class="sub"><td>Batch ${escapeHtml(item.batchNumber)} · Exp ${escapeHtml(formatExpiry(item.expiryDate))}${item.hsnCode ? ` · HSN ${escapeHtml(item.hsnCode)}` : ''}<br/>
          ${escapeHtml(formatQty(item.quantity))} × ${escapeHtml(formatInr(item.unitMrp))} · GST ${escapeHtml(formatPercent(item.gstRate))}</td>
          <td class="right">${escapeHtml(formatInr(item.netAmount))}</td></tr>`,
    )
    .join('');
  const substituted = sale.items.filter((i) => i.isSubstitution);
  const gst = gstSummaryByRate(sale.items);
  const interState = sale.igstAmount !== 0;
  const payments = sale.payments
    .map(
      (p) =>
        `<tr><td>${p.isRefund ? 'Refund' : 'Paid'} by ${escapeHtml(paymentModeLabel[p.mode])}${p.reference ? ` (${escapeHtml(p.reference)})` : ''}</td><td class="right">${escapeHtml(formatInr(p.isRefund ? -p.amount : p.amount))}</td></tr>`,
    )
    .join('');
  const body = `
    <div class="receipt">
      ${letterheadHtml(letterhead, template, { compact: true, taxIds: true })}
      <div class="center title">PHARMACY TAX INVOICE</div>
      ${options.copyLabel ? `<div class="center small">${escapeHtml(options.copyLabel)}</div>` : ''}
      ${sale.status === 'Cancelled' ? `<div class="cancelled">CANCELLED${sale.cancelReason ? `: ${escapeHtml(sale.cancelReason)}` : ''}</div>` : ''}
      <table class="small">
        <tr><td>Invoice</td><td class="right"><b>${escapeHtml(sale.invoiceNumber)}</b></td></tr>
        <tr><td>Date</td><td class="right">${escapeHtml(formatDateTime(sale.soldAt))}</td></tr>
        <tr><td>${sale.buyerGstin ? 'Buyer' : 'Patient'}</td><td class="right">${customer}</td></tr>
        ${sale.buyerGstin ? `<tr><td>Buyer GSTIN</td><td class="right">${escapeHtml(sale.buyerGstin)}</td></tr>` : ''}
        ${sale.placeOfSupply ? `<tr><td>Place of supply</td><td class="right">${escapeHtml(gstStateName(sale.placeOfSupply) ?? sale.placeOfSupply)}</td></tr>` : ''}
        ${sale.prescriberName ? `<tr><td>Prescriber</td><td class="right">${escapeHtml(sale.prescriberName)}${sale.prescriberRegistrationNumber ? ` (Reg. ${escapeHtml(sale.prescriberRegistrationNumber)})` : ''}</td></tr>` : ''}
        ${sale.prescriptionNumber ? `<tr><td>Prescription</td><td class="right">${escapeHtml(sale.prescriptionNumber)}</td></tr>` : ''}
      </table>
      <div class="dash"></div>
      <table>${rows}</table>
      <div class="dash"></div>
      <table class="totals">
        <tr><td>MRP value</td><td class="right">${escapeHtml(formatInr(sale.grossAmount))}</td></tr>
        ${sale.discountAmount ? `<tr><td>${sale.freeTreatmentReason ? `Free treatment by law (${escapeHtml(sale.freeTreatmentReason)})` : `Discount (${escapeHtml(formatPercent(sale.discountPercent))})`}</td><td class="right">−${escapeHtml(formatInr(sale.discountAmount))}</td></tr>` : ''}
        <tr><td>Taxable value</td><td class="right">${escapeHtml(formatInr(sale.taxableAmount))}</td></tr>
        ${
          interState
            ? `<tr><td>IGST</td><td class="right">${escapeHtml(formatInr(sale.igstAmount))}</td></tr>`
            : `<tr><td>CGST</td><td class="right">${escapeHtml(formatInr(sale.cgstAmount))}</td></tr>
        <tr><td>SGST</td><td class="right">${escapeHtml(formatInr(sale.sgstAmount))}</td></tr>`
        }
        ${sale.roundOff ? `<tr><td>Round off</td><td class="right">${escapeHtml(formatInr(sale.roundOff))}</td></tr>` : ''}
        <tr class="net"><td>Net amount</td><td class="right">${escapeHtml(formatInr(sale.netAmount))}</td></tr>
        ${payments}
        ${sale.dueAmount > 0 ? `<tr><td><b>Balance due</b></td><td class="right"><b>${escapeHtml(formatInr(sale.dueAmount))}</b></td></tr>` : ''}
        ${sale.returnedAmount > 0 ? `<tr><td>Returned</td><td class="right">${escapeHtml(formatInr(sale.returnedAmount))}</td></tr>` : ''}
      </table>
      ${
        gst.length > 0
          ? `<div class="dash"></div><table class="gst"><tr><th>GST</th><th>Taxable</th>${interState ? '<th>IGST</th>' : '<th>CGST</th><th>SGST</th>'}</tr>${gst
              .map(
                (g) =>
                  `<tr><td>${escapeHtml(formatPercent(g.rate))}</td><td>${escapeHtml(formatInr(g.taxable))}</td>${interState ? `<td>${escapeHtml(formatInr(g.igst))}</td>` : `<td>${escapeHtml(formatInr(g.cgst))}</td><td>${escapeHtml(formatInr(g.sgst))}</td>`}</tr>`,
              )
              .join('')}</table>`
          : ''
      }
      ${
        sale.eInvoice
          ? `<div class="dash"></div><div class="center"><img src="${escapeHtml(sale.eInvoice.qrCodeSvg)}" alt="Signed QR code of the e-invoice" style="width:32mm;height:32mm"></div>
      <div class="small" style="word-break:break-all">IRN ${escapeHtml(sale.eInvoice.irn)}</div>
      <div class="small">Ack no. ${escapeHtml(sale.eInvoice.ackNo)} · ${escapeHtml(formatDateTime(sale.eInvoice.ackDate))}</div>`
          : ''
      }
      ${substituted.length > 0 ? `<div class="small muted">* Substituted with the prescriber's consent for: ${substituted.map((i) => escapeHtml(i.prescribedDrugName ?? '')).join(', ')}</div>` : ''}
      ${sale.counsellingNotes ? `<div class="dash"></div><div class="small"><b>Advice:</b> ${escapeHtml(sale.counsellingNotes)}</div>` : ''}
      <div class="dash"></div>
      <div class="small center">${sale.soldBy ? `Pharmacist: ${escapeHtml(sale.soldBy)} · ` : ''}${escapeHtml(sale.storeName)}</div>
      <div class="small center">Keep medicines out of reach of children. Get well soon.</div>
    </div>`;
  return printDocument(body, {
    title: `Invoice ${sale.invoiceNumber}`,
    pageSize: 'Receipt80',
    styles: RECEIPT_STYLES,
    template,
  });
}

/** Dispensing label for one medicine (50 × 25 mm). */
export interface DrugLabel {
  patientName: string;
  drugName: string;
  quantity: number;
  unit?: string | null;
  directions?: string | null;
  batchNumber?: string | null;
  expiryDate?: string | null;
  dispensedOn: string;
}

/** Plain directions from the prescription line: "1 tab · twice a day · 5 days · after food". */
export function directionsText(line: {
  dose?: string | null;
  frequency?: string | null;
  durationDays?: number | null;
  route?: string | null;
  instructions?: string | null;
}): string {
  const days = line.durationDays ? `${line.durationDays} ${line.durationDays === 1 ? 'day' : 'days'}` : null;
  return [line.dose, line.frequency, days, line.route, line.instructions]
    .map((part) => (typeof part === 'string' ? part.trim() : part))
    .filter(Boolean)
    .join(' · ');
}

const LABEL_STYLES = `
  .label { width: 46mm; height: 21mm; overflow: hidden; font-size: 7.5px; line-height: 1.25; page-break-after: always; break-after: page; }
  .label:last-child { page-break-after: auto; break-after: auto; }
  .patient { font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .drug { font-weight: 800; font-size: 8.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .dir { max-height: 2.6em; overflow: hidden; }
  .foot { display: flex; justify-content: space-between; color: #333; }`;

export function buildLabelsHtml(labels: readonly DrugLabel[], hospitalName: string): string {
  const body = labels
    .map(
      (l) => `
      <div class="label">
        <div class="patient">${escapeHtml(l.patientName)}</div>
        <div class="drug">${escapeHtml(l.drugName)} × ${escapeHtml(formatQty(l.quantity, l.unit))}</div>
        ${l.directions ? `<div class="dir">${escapeHtml(l.directions)}</div>` : ''}
        <div class="foot"><span>${l.batchNumber ? `B ${escapeHtml(l.batchNumber)}` : ''}${l.expiryDate ? ` · Exp ${escapeHtml(formatExpiry(l.expiryDate))}` : ''}</span><span>${escapeHtml(formatDateOnly(l.dispensedOn))}</span></div>
        <div class="foot"><span>${escapeHtml(hospitalName)}</span></div>
      </div>`,
    )
    .join('');
  return printDocument(body, { title: 'Medicine labels', pageSize: 'Label50x25', styles: LABEL_STYLES });
}

/** A5 slip for medicines sent to the ward (charged to the IP account). */
export function buildIpIssueSlipHtml(
  issue: IpIssueDetail,
  letterhead: Letterhead,
  ward?: { wardName?: string | null; bedLabel?: string | null },
): string {
  const rows = issue.lines
    .map(
      (
        l,
        index,
      ) => `<tr><td>${index + 1}</td><td>${escapeHtml(l.itemName)}</td><td>${escapeHtml(l.batchNumber)}</td><td>${escapeHtml(formatExpiry(l.expiryDate))}</td>
        <td class="right">${escapeHtml(formatQty(l.quantity))}</td><td class="right">${escapeHtml(formatInr(l.unitMrp))}</td><td class="right">${escapeHtml(formatInr(l.amount))}</td></tr>`,
    )
    .join('');
  const template = templateFor(letterhead, 'other');
  const body = `
    ${letterheadHtml(letterhead, template)}
    <h2 class="center" style="margin-top:6px">Ward issue slip</h2>
    <table class="meta">
      <tr><td>Issue</td><td><b>${escapeHtml(issue.number)}</b></td><td>Date</td><td>${escapeHtml(formatDateTime(issue.issuedAt))}</td></tr>
      <tr><td>Patient</td><td>${escapeHtml(issue.patient.fullName)} (${escapeHtml(issue.patient.uhid)})</td><td>Ward / bed</td><td>${escapeHtml([ward?.wardName, ward?.bedLabel].filter(Boolean).join(' / ') || '—')}</td></tr>
    </table>
    <div class="rule"></div>
    <table class="lines"><tr><th>#</th><th>Medicine</th><th>Batch</th><th>Expiry</th><th class="right">Qty</th><th class="right">MRP</th><th class="right">Amount</th></tr>${rows}</table>
    <div class="rule"></div>
    <table><tr><td>Charged to the in-patient account</td><td class="right"><b>${escapeHtml(formatInr(issue.totalAmount))}</b></td></tr></table>
    <div style="display:flex;justify-content:space-between;margin-top:28px">
      <div>Issued by: ${escapeHtml(issue.issuedBy ?? '')}</div><div>Received by (ward nurse): ____________________</div>
    </div>`;
  const styles = `.small{font-size:10px}.meta td{padding:2px 6px 2px 0}.lines th{border-bottom:1px solid #999}.lines td{border-bottom:1px solid #eee}`;
  return printDocument(body, { title: `Ward issue ${issue.number}`, pageSize: 'A5', styles, template });
}

/** Counter receipt: straight to this computer's receipt printer on HC360 Desktop, print dialog in a browser. */
export async function printReceipt(
  sale: PharmacySaleDetail,
  letterhead: Letterhead,
  copyLabel?: string,
): Promise<void> {
  await printHtml(buildReceiptHtml(sale, letterhead, { copyLabel }), { pageSize: 'Receipt80', silent: true });
}

/** A medicine label as the server writes it for a ZPL or TSPL label printer: the same facts as the label page. */
export function dispensingLabelData(label: DrugLabel, hospitalName: string): DispensingLabel {
  return {
    patientName: label.patientName,
    medicine: label.drugName,
    quantity: formatQty(label.quantity, label.unit),
    directions: label.directions || null,
    batch: label.batchNumber || null,
    expiry: label.expiryDate ?? null,
    dispensedOn: label.dispensedOn,
    hospital: hospitalName || null,
  };
}

/** One label per medicine on this computer's label printer, without a dialog. */
export async function printLabels(labels: readonly DrugLabel[], hospitalName: string): Promise<void> {
  if (labels.length === 0) {
    return;
  }
  await printLabelJob({
    kind: 'dispensing',
    labels: labels.map((label) => dispensingLabelData(label, hospitalName)),
    page: () => buildLabelsHtml(labels, hospitalName),
  });
}

export async function printIpIssueSlip(
  issue: IpIssueDetail,
  letterhead: Letterhead,
  ward?: { wardName?: string | null; bedLabel?: string | null },
): Promise<void> {
  await printHtml(buildIpIssueSlipHtml(issue, letterhead, ward), { pageSize: 'A5' });
}
