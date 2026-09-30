import { escapeHtml, formatDateTime, formatInr, printDocument, printHtml } from '@hc360/core';
import type { CountDetail, IndentDetail, IndentIssueDto, TransferDetail } from './api';
import { formatExpiry, formatQty } from './format';

/** Hospital heading for store documents. */
export interface StoreLetterhead {
  name: string;
  address?: string | null;
  gstin?: string | null;
}

const STYLES = `
  .hospital { font-weight: 700; font-size: 15px; }
  .small { font-size: 10px; color: #444; }
  .meta td { padding: 2px 10px 2px 0; }
  .lines th { border-bottom: 1px solid #888; font-size: 11px; }
  .lines td { border-bottom: 1px solid #e5e5e5; }
  .sign { display: flex; justify-content: space-between; margin-top: 36px; }`;

function heading(letterhead: StoreLetterhead, title: string): string {
  return `
    <div class="center hospital">${escapeHtml(letterhead.name)}</div>
    ${letterhead.address ? `<div class="center small">${escapeHtml(letterhead.address)}</div>` : ''}
    ${letterhead.gstin ? `<div class="center small">GSTIN ${escapeHtml(letterhead.gstin)}</div>` : ''}
    <h2 class="center" style="margin-top:8px">${escapeHtml(title)}</h2>`;
}

/** Issue slip that travels with goods issued against an indent. */
export function buildIndentIssueHtml(
  issue: IndentIssueDto,
  indent: Pick<IndentDetail, 'number' | 'fromStoreName' | 'toStoreName' | 'departmentName'>,
  letterhead: StoreLetterhead,
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
  return printDocument(body, { title: `Issue ${issue.number}`, pageSize: 'A5', styles: STYLES });
}

/** Stock transfer note (goods in transit between stores). */
export function buildTransferNoteHtml(transfer: TransferDetail, letterhead: StoreLetterhead): string {
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
  return printDocument(body, { title: `Transfer ${transfer.number}`, pageSize: 'A5', styles: STYLES });
}

export async function printIndentIssue(
  issue: IndentIssueDto,
  indent: Pick<IndentDetail, 'number' | 'fromStoreName' | 'toStoreName' | 'departmentName'>,
  letterhead: StoreLetterhead,
): Promise<void> {
  await printHtml(buildIndentIssueHtml(issue, indent, letterhead), { pageSize: 'A5' });
}

export async function printTransferNote(transfer: TransferDetail, letterhead: StoreLetterhead): Promise<void> {
  await printHtml(buildTransferNoteHtml(transfer, letterhead), { pageSize: 'A5' });
}

/**
 * Blind count sheet for the counters: item, batch and expiry with blank columns to write what is on the shelf.
 * Book quantities are left off so the count is not copied from the system.
 */
export function buildCountSheetHtml(count: CountDetail, letterhead: StoreLetterhead): string {
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
  return printDocument(body, { title: `Count ${count.number}`, pageSize: 'A4', styles });
}

export async function printCountSheet(count: CountDetail, letterhead: StoreLetterhead): Promise<void> {
  await printHtml(buildCountSheetHtml(count, letterhead), { pageSize: 'A4' });
}
