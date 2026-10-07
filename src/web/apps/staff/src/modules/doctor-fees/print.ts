import {
  escapeHtml,
  formatDateOnly,
  formatDateTime,
  formatInr,
  letterheadHtml,
  printDocument,
  templateFor,
  type Letterhead,
} from '@hc360/core';
import { PAYMENT_MODE_LABEL, SERVICE_CATEGORY_LABEL } from '@/modules/billing/labels';
import { amountInWords } from '@/modules/billing/money';
import { ACCRUAL_STATUS_LABEL, PAYOUT_STATUS_LABEL, type DoctorStatement, type PayoutDto } from './api';

/**
 * Doctor fee prints (A4): the statement for a period and the payout advice, with the "Other documents" print template.
 * Every value is escaped.
 */

const e = escapeHtml;
const money = (value: number) => e(formatInr(value));

const STYLES = `
  .h-title { font-size: 15px; font-weight: 700; text-transform: uppercase; margin: 6px 0; }
  .box { border: 1px solid #bbb; border-radius: 4px; padding: 6px 8px; margin: 8px 0; }
  table.lines th { border-bottom: 1px solid #555; background: #f2f2f2; font-size: 10.5px; }
  table.lines td { border-bottom: 1px solid #e3e3e3; font-size: 10.5px; }
  .num { text-align: right; white-space: nowrap; }
  .totals { width: 50%; margin-left: auto; margin-top: 8px; }
  .totals .strong td { font-weight: 700; border-top: 1px solid #555; }
  .sign { margin-top: 36px; display: flex; justify-content: space-between; }
  .sign div { border-top: 1px solid #555; padding-top: 2px; min-width: 180px; text-align: center; }
  .foot { margin-top: 12px; font-size: 9.5px; color: #555; }`;

/** The letterhead, with GSTIN and PAN for the tax deducted, and the title. */
function header(letterhead: Letterhead, title: string): string {
  return `${letterheadHtml(letterhead, templateFor(letterhead, 'other'), { taxIds: true })}
    <div class="h-title">${e(title)}</div>`;
}

function page(body: string, title: string, letterhead: Letterhead): string {
  return printDocument(body, { title, pageSize: 'A4', styles: STYLES, template: templateFor(letterhead, 'other') });
}

/** Doctor fee statement for a period (DFA-04). */
export function buildStatementHtml(statement: DoctorStatement, letterhead: Letterhead, printedAt: string): string {
  const accruals = statement.accruals
    .map(
      (a) => `<tr>
        <td>${e(formatDateOnly(a.accruedOn))}</td>
        <td>${e(a.billNumber)}</td>
        <td>${e(a.description)}${a.isReversal ? ' (reversal)' : ''}<div class="muted">${e(a.serviceCode)} · ${e(SERVICE_CATEGORY_LABEL[a.category])}</div></td>
        <td class="num">${money(a.basisAmount)}</td>
        <td class="num">${a.method === 'Percentage' ? `${e(a.rate)}%` : money(a.rate)}</td>
        <td class="num">${money(a.shareAmount)}</td>
        <td>${e(ACCRUAL_STATUS_LABEL[a.status])}</td>
      </tr>`,
    )
    .join('');
  const payouts = statement.payouts
    .map(
      (
        p,
      ) => `<tr><td>${e(p.payoutNumber)}</td><td>${e(formatDateOnly(p.periodFrom))} – ${e(formatDateOnly(p.periodTo))}</td>
        <td class="num">${money(p.grossAmount)}</td><td class="num">${money(p.deductionAmount)}</td><td class="num">${money(p.tdsAmount)}</td><td class="num">${money(p.netAmount)}</td><td>${e(PAYOUT_STATUS_LABEL[p.status])}</td></tr>`,
    )
    .join('');
  const body = `${header(letterhead, 'Doctor fee statement')}
    <div class="box"><b>${e(statement.practitionerName ?? 'Doctor')}</b> · ${e(formatDateOnly(statement.from))} to ${e(formatDateOnly(statement.to))}</div>
    <table class="totals">
      <tr><td>Fees earned</td><td class="num">${money(statement.accrued)}</td></tr>
      <tr><td>Reversed (cancelled bills)</td><td class="num">${money(statement.reversed)}</td></tr>
      <tr><td>In a payout</td><td class="num">${money(statement.inPayout)}</td></tr>
      <tr><td>Paid out</td><td class="num">${money(statement.paidOut)}</td></tr>
      <tr class="strong"><td>Unpaid</td><td class="num">${money(statement.unpaid)}</td></tr>
    </table>
    <h3>Fees by bill line</h3>
    <table class="lines"><tr><th>Date</th><th>Bill</th><th>Service</th><th class="num">Billed value</th><th class="num">Rate</th><th class="num">Share</th><th>Status</th></tr>${accruals || '<tr><td colspan="7">No fees in this period.</td></tr>'}</table>
    ${payouts ? `<h3>Payouts</h3><table class="lines"><tr><th>Payout</th><th>Period</th><th class="num">Gross</th><th class="num">Deductions</th><th class="num">TDS</th><th class="num">Net</th><th>Status</th></tr>${payouts}</table>` : ''}
    <div class="foot">Printed ${e(formatDateTime(printedAt))}. TDS is deducted under section 194J.</div>`;
  return page(body, `Fee statement ${statement.practitionerName ?? ''}`.trim(), letterhead);
}

/** Payout advice given to the doctor with the payment. */
export function buildPayoutAdviceHtml(payout: PayoutDto, letterhead: Letterhead): string {
  const deductions = payout.deductions
    .map((d) => `<tr><td>Less: ${e(d.description)}</td><td class="num">${money(-d.amount)}</td></tr>`)
    .join('');
  const body = `${header(letterhead, 'Doctor payout advice')}
    <div class="box">
      <div>Payout <b>${e(payout.payoutNumber)}</b> · ${e(PAYOUT_STATUS_LABEL[payout.status])}</div>
      <div>Paid to <b>${e(payout.practitionerName ?? 'Doctor')}</b> for ${e(formatDateOnly(payout.periodFrom))} to ${e(formatDateOnly(payout.periodTo))} (${e(payout.accrualCount)} fee lines)</div>
      ${payout.paidAt ? `<div>Paid on ${e(formatDateTime(payout.paidAt))}${payout.paymentMode ? ` by ${e(PAYMENT_MODE_LABEL[payout.paymentMode])}` : ''}${payout.paidReference ? `, reference ${e(payout.paidReference)}` : ''}</div>` : ''}
    </div>
    <table class="totals">
      <tr><td>Fees for the period</td><td class="num">${money(payout.grossAmount)}</td></tr>
      ${deductions}
      <tr><td>Less: TDS ${e(payout.tdsPercent)}% (section 194J)</td><td class="num">${money(-payout.tdsAmount)}</td></tr>
      <tr class="strong"><td>Net paid</td><td class="num">${money(payout.netAmount)}</td></tr>
    </table>
    <div class="foot">${e(amountInWords(payout.netAmount))}</div>
    ${payout.notes ? `<div class="foot">${e(payout.notes)}</div>` : ''}
    <div class="sign"><div>Received by the doctor</div><div>Accounts</div></div>`;
  return page(body, `Payout ${payout.payoutNumber}`, letterhead);
}
