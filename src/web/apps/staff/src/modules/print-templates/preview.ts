import {
  escapeHtml,
  letterheadHtml,
  printDocument,
  stationeryMarginMm,
  type Letterhead,
  type PrintTemplate,
  type PrintTemplateKey,
} from '@hc360/core';
import type { PrintTemplateItem } from './api';

/** A title of each kind of document, for the sample page. */
const SAMPLE_TITLE: Record<PrintTemplateKey, string> = {
  bill: 'Tax invoice',
  receipt: 'Payment receipt',
  'sales-invoice': 'Pharmacy tax invoice',
  report: 'Laboratory report',
  prescription: 'Prescription',
  'clinical-note': 'Discharge summary',
  purchase: 'Purchase order',
  other: 'Day-end collection report',
};

/** Documents that print the hospital's GSTIN and PAN whatever the paper. */
const TAX_DOCUMENTS = new Set<PrintTemplateKey>(['bill', 'receipt', 'sales-invoice', 'purchase']);

const PAPER: Record<'A4' | 'A5' | 'Receipt80', { width: string; height: string; margin: number }> = {
  A4: { width: '210mm', height: '297mm', margin: 12 },
  A5: { width: '148mm', height: '210mm', margin: 12 },
  Receipt80: { width: '80mm', height: 'auto', margin: 2 },
};

/**
 * A sample page with the template's letterhead, blank space and closing note, as the page prints them. The document's
 * own content is a placeholder: every document keeps its layout. On screen the paper is drawn with its margins, and
 * the space left for pre-printed stationery is outlined.
 */
export function templatePreviewHtml(item: PrintTemplateItem, template: PrintTemplate, letterhead: Letterhead): string {
  const roll = !item.allowsPreprinted;
  const pageSize: keyof typeof PAPER = roll ? 'Receipt80' : (template.paper ?? item.papers[0] ?? 'A4');
  const paper = PAPER[pageSize];
  const { top, bottom } = roll ? { top: 0, bottom: 0 } : stationeryMarginMm(template);
  const title = escapeHtml(SAMPLE_TITLE[item.key]);
  const taxIds = TAX_DOCUMENTS.has(item.key);

  const header = roll
    ? `${letterheadHtml(letterhead, template, { compact: true, taxIds })}<div class="sample-title center">${title}</div>`
    : letterheadHtml(letterhead, template, {
        taxIds,
        registration: taxIds,
        aside: `<div class="sample-title">${title}</div>`,
      });
  const stationery = [
    top > 0 ? `<div class="space" style="top:${paper.margin}mm;height:${top}mm">The stationery's letterhead</div>` : '',
    bottom > 0
      ? `<div class="space" style="bottom:${paper.margin}mm;height:${bottom}mm">The stationery's footer</div>`
      : '',
  ].join('');
  const body = `${stationery}${header}
    <div class="sample">
      <p>The ${escapeHtml(item.name.toLowerCase())} print their own content here, as they do now.</p>
      <div class="line"></div><div class="line"></div><div class="line short"></div>
    </div>`;
  const styles = `
    .sample-title { font-weight: 700; text-transform: uppercase; letter-spacing: .5px; }
    .sample { color: #616161; margin-top: 8px; }
    .line { height: 8px; border-radius: 4px; background: #e6e6e6; margin: 8px 0; }
    .line.short { width: 60%; }
    @media screen {
      html { background: #e9e9e9; }
      body {
        position: relative;
        width: ${paper.width};
        min-height: ${paper.height};
        margin: 12px auto;
        padding: ${paper.margin + top}mm ${paper.margin}mm ${paper.margin + bottom}mm;
        background: #fff;
        box-shadow: 0 1px 4px rgba(0, 0, 0, .25);
      }
      .space {
        position: absolute;
        left: ${paper.margin}mm;
        right: ${paper.margin}mm;
        display: flex;
        align-items: center;
        justify-content: center;
        border: 1px dashed #8a8a8a;
        color: #616161;
        font-size: 11px;
      }
    }
    @media print { .space { display: none; } }`;
  return printDocument(body, { title: item.name, pageSize, styles, template });
}
