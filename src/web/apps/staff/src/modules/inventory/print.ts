import {
  escapeHtml,
  formatDateTime,
  formatInr,
  letterheadHtml,
  printDocument,
  printHtml,
  templateFor,
  type Letterhead,
  type PageSize,
} from '@hc360/core';
import type { CountDetail, IndentDetail, IndentIssueDto, TransferDetail } from './api';
import { formatExpiry, formatQty } from './format';

/** Store documents (issue slips, transfer notes, count sheets), with the "Other documents" print template. */

const STYLES = `
  .small { font-size: 10px; color: #444; }
  .meta td { padding: 2px 10px 2px 0; }
  .lines th { border-bottom: 1px solid #888; font-size: 11px; }
  .lines td { border-bottom: 1px solid #e5e5e5; }
  .sign { display: flex; justify-content: space-between; margin-top: 36px; }`;

/** The letterhead with GSTIN and PAN (goods moved between branches), and the title. */
function heading(letterhead: Letterhead, title: string): string {
  return `${letterheadHtml(letterhead, templateFor(letterhead, 'other'), { taxIds: true })}
    <h2 class="center" style="margin-top:8px">${escapeHtml(title)}</h2>`;
}

function page(body: string, title: string, pageSize: PageSize, letterhead: Letterhead, styles = STYLES): string {
  return printDocument(body, { title, pageSize, styles, template: templateFor(letterhead, 'other') });
}

/** Issue slip that travels with goods issued against an indent. */
export function buildIndentIssueHtml(
  issue: IndentIssueDto,
  indent: Pick<IndentDetail, 'number' | 'fromStoreName' | 'toStoreName' | 'departmentName'>,
  letterhead: Letterhead,
): string {
  const rows = issue.lines
    .map(
      (
        l,
        i,
      ) => `<tr><td>${i + 1}</td><td>${escapeHtml(l.itemName)}</td><td>${escapeHtml(l.batchNumber)}</td><td>${escapeHtml(formatExpiry(l.expiryDate))}</td>
        <td class="right">${escapeHtml(formatQty(l.quantity))}</td><td class="right">${escapeHtml(formatInr(l.unitCost))}</td><td class="right">${escapeHtml(formatInr(l.value))}</td></tr>`,
    )
    .join('');
  const body = `
    ${heading(letterhead, 'Store issue slip')}
    <table class="meta">
      <tr><td>Issue</td><td><b>${escapeHtml(issue.number)}</b></td><td>Date</td><td>${escapeHtml(formatDateTime(issue.issuedAt))}</td></tr>
      <tr><td>Indent</td><td>${escapeHtml(indent.number)}</td><td>From</td><td>${escapeHtml(indent.fromStoreName)}</td></tr>
      <tr><td>To</td><td>${escapeHtml(indent.toStoreName ?? indent.departmentName ?? '—')}</td><td>Received by</td><td>${escapeHtml(issue.receivedByName ?? '')}</td></tr>
    </table>
    <div class="rule"></div>
    <table class="lines"><tr><th>#</th><th>Item</th><th>Batch</th><th>Expiry</th><th class="right">Qty</th><th class="right">Unit cost</th><th class="right">Value</th></tr>${rows}</table>
    <div class="rule"></div>
    <table><tr><td>Total value at cost</td><td class="right"><b>${escapeHtml(formatInr(issue.totalValue))}</b></td></tr></table>
    ${issue.remarks ? `<p>${escapeHtml(issue.remarks)}</p>` : ''}
    <div class="sign"><div>Issued by: ${escapeHtml(issue.issuedBy ?? '')}</div><div>Received by: ____________________</div></div>`;
  return page(body, `Issue ${issue.number}`, 'A5', letterhead);
}

/** Stock transfer note (goods in transit between stores). */
export function buildTransferNoteHtml(transfer: TransferDetail, letterhead: Letterhead): string {
  const rows = transfer.lines
    .map(
      (
        l,
        i,
      ) => `<tr><td>${i + 1}</td><td>${escapeHtml(l.itemName)}</td><td>${escapeHtml(l.batchNumber)}</td><td>${escapeHtml(formatExpiry(l.expiryDate))}</td>
        <td class="right">${escapeHtml(formatQty(l.quantity))}</td><td class="right">${escapeHtml(formatInr(l.mrp))}</td></tr>`,
    )
    .join('');
  const body = `
    ${heading(letterhead, 'Stock transfer note')}
    <table class="meta">
      <tr><td>Transfer</td><td><b>${escapeHtml(transfer.number)}</b></td><td>Sent</td><td>${escapeHtml(formatDateTime(transfer.dispatchedAt))}</td></tr>
      <tr><td>From</td><td>${escapeHtml(transfer.fromStoreName)}</td><td>To</td><td>${escapeHtml(transfer.toStoreName)}</td></tr>
    </table>
    <div class="rule"></div>
    <table class="lines"><tr><th>#</th><th>Item</th><th>Batch</th><th>Expiry</th><th class="right">Qty</th><th class="right">MRP</th></tr>${rows}</table>
    <div class="rule"></div>
    <table><tr><td>Value at cost</td><td class="right"><b>${escapeHtml(formatInr(transfer.totalValue))}</b></td></tr></table>
    ${transfer.remarks ? `<p>${escapeHtml(transfer.remarks)}</p>` : ''}
    <div class="sign"><div>Sent by: ____________________</div><div>Received by: ____________________</div></div>`;
  return page(body, `Transfer ${transfer.number}`, 'A5', letterhead);
}

export async function printIndentIssue(
  issue: IndentIssueDto,
  indent: Pick<IndentDetail, 'number' | 'fromStoreName' | 'toStoreName' | 'departmentName'>,
  letterhead: Letterhead,
): Promise<void> {
  await printHtml(buildIndentIssueHtml(issue, indent, letterhead), { pageSize: 'A5' });
}

export async function printTransferNote(transfer: TransferDetail, letterhead: Letterhead): Promise<void> {
  await printHtml(buildTransferNoteHtml(transfer, letterhead), { pageSize: 'A5' });
}

/**
 * Blind count sheet for the counters: item, batch and expiry with blank columns to write what is on the shelf.
 * Book quantities are left off so the count is not copied from the system.
 */
export function buildCountSheetHtml(count: CountDetail, letterhead: Letterhead): string {
  const rows = [...count.lines]
    .sort((a, b) => a.itemName.localeCompare(b.itemName) || a.batchNumber.localeCompare(b.batchNumber))
    .map(
      (
        l,
        i,
      ) => `<tr><td>${i + 1}</td><td>${escapeHtml(l.itemName)}</td><td>${escapeHtml(l.batchNumber)}</td><td>${escapeHtml(formatExpiry(l.expiryDate))}</td>
        <td class="blank"></td><td class="blank"></td></tr>`,
    )
    .join('');
  const body = `
    ${heading(letterhead, 'Stock count sheet')}
    <table class="meta">
      <tr><td>Count</td><td><b>${escapeHtml(count.number)}</b></td><td>Store</td><td>${escapeHtml(count.storeName)}</td></tr>
      <tr><td>Started</td><td>${escapeHtml(formatDateTime(count.startedAt))}</td><td>Batches</td><td>${count.lines.length}</td></tr>
    </table>
    <div class="rule"></div>
    <table class="lines"><tr><th>#</th><th>Item</th><th>Batch</th><th>Expiry</th><th>Counted</th><th>Remarks</th></tr>${rows}</table>
    <div class="sign"><div>Counted by: ____________________</div><div>Checked by: ____________________</div></div>`;
  const styles = `${STYLES} .blank { width: 22%; border-bottom: 1px solid #999 !important; }`;
  return page(body, `Count ${count.number}`, 'A4', letterhead, styles);
}

export async function printCountSheet(count: CountDetail, letterhead: Letterhead): Promise<void> {
  await printHtml(buildCountSheetHtml(count, letterhead), { pageSize: 'A4' });
}
