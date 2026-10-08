import { escapeHtml, printDocument, type PharmacyTagLabel } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { printLabelJob } from '@/lib/labelPrinting';
import { formatExpiry } from '@/modules/inventory/format';
import type { ScannedBatch } from './api';

/**
 * Pharmacy tags (INT-BAR): a label for a strip, bottle, box or shelf with the medicine, its batch, expiry and MRP, and
 * a Code 128 barcode of the item code and batch number joined by `#` (`PCM500#AB1234`). Scanning it at the counter or
 * in the dispensing queue picks that batch, rather than leaving it to first-expiry-first-out. Item codes never hold a
 * `#`, so the server splits the value unambiguously (`BatchTag` in SupplyChain).
 */

export const TAG_SEPARATOR = '#';

/** Longest value a label barcode takes (Code128.MaxLength). */
const MAX_BARCODE = 48;

/** Printable ASCII except what label printers' command languages reserve: ! > " \ (Code128.IsEncodable). */
const ENCODABLE = /^[ -~]+$/;
const RESERVED = /[!>"\\]/;

/** The most labels one print sends (LabelRules.MaxLabels). */
export const MAX_TAGS = 100;

/** The barcode on a tag: item code and batch, or the code alone when there is no batch or it cannot be coded. */
export function batchTagValue(itemCode: string, batchNumber?: string | null): string {
  const code = itemCode.trim().toUpperCase();
  const batch = batchNumber?.trim() ?? '';
  const value = `${code}${TAG_SEPARATOR}${batch}`;
  return batch && value.length <= MAX_BARCODE && ENCODABLE.test(value) && !RESERVED.test(value) ? value : code;
}

/** Whether scanned text is a batch tag (the same rule as `BatchTag.TryParse` on the server). */
export function isBatchTag(text: string): boolean {
  const value = text.trim();
  const at = value.indexOf(TAG_SEPARATOR);
  if (at <= 0 || at === value.length - 1) {
    return false;
  }
  const code = value.slice(0, at).trim();
  const batch = value.slice(at + 1).trim();
  return code.length <= 32 && /^[A-Za-z0-9_/-]+$/.test(code) && batch.length > 0 && batch.length <= 40;
}

/** One kind of tag to print, and how many. */
export interface PharmacyTag {
  itemCode: string;
  medicine: string;
  batchNumber?: string | null;
  /** yyyy-MM-dd */
  expiryDate?: string | null;
  /** MRP per unit, as the store counts the stock. */
  mrp?: number | null;
  unit?: string | null;
  copies: number;
}

/** A tag as the server writes it for a ZPL or TSPL label printer: the same facts as the tag page. */
export function pharmacyTagData(tag: PharmacyTag): PharmacyTagLabel {
  return {
    barcode: batchTagValue(tag.itemCode, tag.batchNumber),
    medicine: tag.medicine,
    batch: tag.batchNumber?.trim() || null,
    expiry: tag.expiryDate ?? null,
    mrp: tag.mrp ?? null,
    unit: tag.unit?.trim() || null,
  };
}

/** How many labels the tags come to, which one print may not take more than {@link MAX_TAGS} of. */
export function tagCount(tags: readonly PharmacyTag[]): number {
  return tags.reduce((total, tag) => total + Math.max(0, Math.floor(tag.copies)), 0);
}

/** Every tag, each as many times as asked, on this computer's label printer without a dialog. */
export async function printPharmacyTags(tags: readonly PharmacyTag[]): Promise<void> {
  const labels = tags.flatMap((tag) =>
    Array.from({ length: Math.max(0, Math.floor(tag.copies)) }, () => pharmacyTagData(tag)),
  );
  if (labels.length === 0) {
    return;
  }
  if (labels.length > MAX_TAGS) {
    throw new Error(`Print at most ${MAX_TAGS} tags at a time.`);
  }
  await printLabelJob({ kind: 'pharmacy-tags', labels, page: () => buildTagsHtml(labels) });
}

const TAG_STYLES = `
  .tag { width: 46mm; height: 21mm; overflow: hidden; text-align: center; font-size: 7.5px; line-height: 1.25; page-break-after: always; break-after: page; }
  .tag:last-child { page-break-after: auto; break-after: auto; }
  .drug { font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .code { font-family: Consolas, monospace; font-size: 8.5px; letter-spacing: 0.5px; }
  svg { width: 100%; height: 8mm; }`;

/** The tag page, for a printer without ZPL or TSPL and for the browser. */
export async function buildTagsHtml(labels: readonly PharmacyTagLabel[]): Promise<string> {
  const pages = await Promise.all(
    labels.map(async (label) => {
      const details = [
        label.batch ? `B ${escapeHtml(label.batch)}` : '',
        label.expiry ? `Exp ${escapeHtml(formatExpiry(label.expiry))}` : '',
      ]
        .filter(Boolean)
        .join('&nbsp;&nbsp;');
      return `<div class="tag">
        <div class="drug">${escapeHtml(label.medicine)}</div>
        ${details ? `<div>${details}</div>` : ''}
        ${label.mrp != null ? `<div>MRP Rs ${escapeHtml(label.mrp.toFixed(2))}${label.unit ? `/${escapeHtml(label.unit)}` : ''}</div>` : ''}
        <div>${await barcodeSvg(label.barcode, 'code128', { height: 8 })}</div>
        <div class="code">${escapeHtml(label.barcode)}</div>
      </div>`;
    }),
  );
  return printDocument(pages.join(''), { title: 'Pharmacy tags', pageSize: 'Label50x25', styles: TAG_STYLES });
}

/** What to tell the pharmacist about a scanned tag's batch: null when it was picked. */
export function scannedBatchProblem(scanned: ScannedBatch | null | undefined): string | null {
  if (!scanned) {
    return null;
  }
  return scanned.batchId ? null : (scanned.problem ?? `Batch ${scanned.batchNumber} cannot be handed out here.`);
}
