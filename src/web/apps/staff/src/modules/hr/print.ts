import { escapeHtml, formatDateTime, formatInr, humanize, printDocument, printHtml } from '@hc360/core';
import type { PayslipDto, PayslipLineDto } from './api';
import { monthLabel } from './shared/dates';

const ONES = [
  '',
  'One',
  'Two',
  'Three',
  'Four',
  'Five',
  'Six',
  'Seven',
  'Eight',
  'Nine',
  'Ten',
  'Eleven',
  'Twelve',
  'Thirteen',
  'Fourteen',
  'Fifteen',
  'Sixteen',
  'Seventeen',
  'Eighteen',
  'Nineteen',
];
const TENS = ['', '', 'Twenty', 'Thirty', 'Forty', 'Fifty', 'Sixty', 'Seventy', 'Eighty', 'Ninety'];

function belowHundred(n: number): string {
  if (n < 20) {
    return ONES[n] ?? '';
  }
  const tens = TENS[Math.floor(n / 10)] ?? '';
  const ones = ONES[n % 10] ?? '';
  return ones ? `${tens}-${ones}` : tens;
}

function belowThousand(n: number): string {
  const hundreds = Math.floor(n / 100);
  const rest = n % 100;
  return [hundreds ? `${ONES[hundreds]} Hundred` : '', rest ? belowHundred(rest) : ''].filter(Boolean).join(' ');
}

/** Whole number in words with Indian grouping (crore, lakh, thousand). */
function indianWords(n: number): string {
  const crore = Math.floor(n / 10_000_000);
  const rest = n % 10_000_000;
  const lakh = Math.floor(rest / 100_000);
  const thousand = Math.floor((rest % 100_000) / 1000);
  const hundreds = rest % 1000;
  return [
    crore ? `${indianWords(crore)} Crore` : '',
    lakh ? `${belowHundred(lakh)} Lakh` : '',
    thousand ? `${belowHundred(thousand)} Thousand` : '',
    hundreds ? belowThousand(hundreds) : '',
  ]
    .filter(Boolean)
    .join(' ');
}

/** Rupees in words with Indian grouping: 145320.5 → "Rupees One Lakh Forty-Five Thousand Three Hundred Twenty and Fifty Paise Only". */
export function amountInWords(amount: number): string {
  const paiseTotal = Math.max(0, Math.round(amount * 100));
  const rupees = Math.floor(paiseTotal / 100);
  const paise = paiseTotal % 100;
  const words = indianWords(rupees) || 'Zero';
  return `Rupees ${words}${paise ? ` and ${belowHundred(paise)} Paise` : ''} Only`;
}

function lines(items: PayslipLineDto[]): string {
  return items
    .map(
      (line) =>
        `<tr><td>${escapeHtml(line.name)}${line.isStatutory ? ' <span class="muted">(statutory)</span>' : ''}</td><td class="right">${escapeHtml(formatInr(line.amount))}</td></tr>`,
    )
    .join('');
}

/** A4 payslip body (every value escaped). */
export function payslipHtml(p: PayslipDto, printedAt = new Date().toISOString()): string {
  const rows = Math.max(p.earnings.length, p.deductions.length);
  const pad = (count: number) => '<tr><td>&nbsp;</td><td></td></tr>'.repeat(Math.max(0, rows - count));
  return `
  <div class="slip">
    <div class="head">
      <div>
        <div class="hospital">${escapeHtml(p.facilityName ?? 'Nana Hospitals')}</div>
        <div class="muted">Salary slip</div>
      </div>
      <div class="period">${escapeHtml(monthLabel(p.year, p.month))}</div>
    </div>
    <table class="info">
      <tr><th>Employee</th><td>${escapeHtml(p.employeeName)}</td><th>Employee code</th><td>${escapeHtml(p.employeeCode)}</td></tr>
      <tr><th>Designation</th><td>${escapeHtml(p.designationName ?? '—')}</td><th>Department</th><td>${escapeHtml(p.departmentName ?? '—')}</td></tr>
      <tr><th>PAN</th><td>${escapeHtml(p.panMasked ?? '—')}</td><th>UAN</th><td>${escapeHtml(p.uan ?? '—')}</td></tr>
      <tr><th>Paid by</th><td>${escapeHtml(humanize(p.paymentMode))}</td><th>Bank account</th><td>${escapeHtml(p.bankAccountMasked ?? '—')}</td></tr>
      <tr><th>Days in month</th><td>${p.daysInMonth}</td><th>Paid days / loss of pay</th><td>${p.payableDays} / ${p.lopDays}</td></tr>
    </table>
    <div class="cols">
      <table class="lines"><thead><tr><th>Earnings</th><th class="right">Amount</th></tr></thead>
        <tbody>${lines(p.earnings)}${pad(p.earnings.length)}</tbody>
        <tfoot><tr><th>Gross earnings</th><th class="right">${escapeHtml(formatInr(p.grossEarnings))}</th></tr></tfoot></table>
      <table class="lines"><thead><tr><th>Deductions</th><th class="right">Amount</th></tr></thead>
        <tbody>${lines(p.deductions)}${pad(p.deductions.length)}</tbody>
        <tfoot><tr><th>Total deductions</th><th class="right">${escapeHtml(formatInr(p.totalDeductions))}</th></tr></tfoot></table>
    </div>
    <div class="net">
      <div>Net pay</div>
      <div class="amount">${escapeHtml(formatInr(p.netPay))}</div>
    </div>
    <div class="words">${escapeHtml(amountInWords(p.netPay))}</div>
    <div class="muted small">Employer contributions (not deducted from salary): PF ${escapeHtml(formatInr(p.employerPf))} on a PF wage of ${escapeHtml(
      formatInr(p.pfWage),
    )} · ESI ${escapeHtml(formatInr(p.employerEsi))} on an ESI wage of ${escapeHtml(formatInr(p.esiWage))}</div>
    <div class="foot muted small">This is a computer-generated salary slip and does not need a signature. Printed ${escapeHtml(formatDateTime(printedAt))}.</div>
  </div>`;
}

const PAYSLIP_STYLES = `
  .slip { font-size: 11px; }
  .head { display: flex; justify-content: space-between; align-items: flex-end; border-bottom: 2px solid #045E80; padding-bottom: 6px; margin-bottom: 10px; }
  .hospital { font-size: 16px; font-weight: 700; color: #045E80; }
  .period { font-size: 14px; font-weight: 600; }
  .info th { width: 18%; color: #555; font-weight: 600; }
  .info td { width: 32%; }
  .info { margin-bottom: 12px; }
  .cols { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  .lines { border: 1px solid #bbb; }
  .lines thead th { background: #eef5f9; border-bottom: 1px solid #bbb; }
  .lines tfoot th { border-top: 1px solid #bbb; }
  .net { display: flex; justify-content: space-between; align-items: center; margin-top: 12px; padding: 8px 10px; background: #eef5f9; border: 1px solid #9cc3d5; font-size: 13px; font-weight: 600; }
  .net .amount { font-size: 16px; font-weight: 700; }
  .words { margin: 6px 0 10px; font-style: italic; }
  .small { font-size: 9.5px; }
  .foot { margin-top: 18px; border-top: 1px solid #ccc; padding-top: 6px; }`;

export async function printPayslip(p: PayslipDto): Promise<void> {
  const title = `Salary slip ${p.employeeCode} ${monthLabel(p.year, p.month)}`;
  await printHtml(printDocument(payslipHtml(p), { title, pageSize: 'A4', styles: PAYSLIP_STYLES }), { pageSize: 'A4' });
}
