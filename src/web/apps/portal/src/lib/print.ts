import { escapeHtml, printDocument } from '@hc360/core';

/**
 * Printing and saving documents (reports, prescriptions, bills, receipts, discharge summaries).
 *
 * The portal prints in the page itself rather than through a hidden iframe: iPhone Safari prints the whole page
 * instead of an iframe, and the in-page copy works the same on Android, iPhone and computers. The print dialog's
 * "Save as PDF" gives the patient a PDF. "Download a copy" saves the same document as a self-contained HTML file.
 *
 * Every value placed in a template (each module's print.ts) goes through `escapeHtml`; nothing else is inserted.
 */

export interface PrintableDocument {
  /** Document title; also the default file name the browser suggests for "Save as PDF". */
  title: string;
  /** Body HTML built from escaped values only. */
  body: string;
  /** Extra CSS for this document. */
  styles?: string;
  /** Download name without extension, made of safe characters only. */
  fileName: string;
}

const ROOT_ID = 'hc360-print-root';

/** Base styles, applied only when printing (so they can never affect the app on screen). */
const BASE_STYLES = `
@page { size: A4; margin: 12mm; }
#${ROOT_ID} { font-family: "Segoe UI", "Noto Sans", "Noto Sans Devanagari", system-ui, sans-serif; color: #111; font-size: 11pt; line-height: 1.4; }
#${ROOT_ID} h1 { font-size: 16pt; margin: 0 0 4px; }
#${ROOT_ID} h2 { font-size: 12.5pt; margin: 14px 0 6px; }
#${ROOT_ID} h3 { font-size: 11.5pt; margin: 10px 0 4px; }
#${ROOT_ID} table { width: 100%; border-collapse: collapse; }
#${ROOT_ID} th, #${ROOT_ID} td { text-align: left; padding: 4px 6px; vertical-align: top; border-bottom: 1px solid #ddd; }
#${ROOT_ID} th { font-size: 9.5pt; color: #444; }
#${ROOT_ID} .muted { color: #555; }
#${ROOT_ID} .right { text-align: right; }
#${ROOT_ID} .small { font-size: 9pt; }
#${ROOT_ID} .rule { border-top: 1px solid #999; margin: 8px 0; }
#${ROOT_ID} .avoid-break { break-inside: avoid; }
`;

function ensureRoot(): HTMLElement {
  let root = document.getElementById(ROOT_ID);
  if (!root) {
    root = document.createElement('div');
    root.id = ROOT_ID;
    root.setAttribute('aria-hidden', 'true');
    document.body.appendChild(root);
  }
  return root;
}

/** Opens the browser's print dialog for the document (which also offers "Save as PDF"). */
export function printPortalDocument(doc: PrintableDocument): void {
  const root = ensureRoot();
  // eslint-disable-next-line no-unsanitized/property -- the modules' templates escape every value, and the styles are their constants
  root.innerHTML = `<style>@media print {${BASE_STYLES}${scopeStyles(doc.styles ?? '')}}</style>${doc.body}`;
  const previousTitle = document.title;
  document.title = doc.title;
  let cleaned = false;
  const cleanup = () => {
    if (cleaned) {
      return;
    }
    cleaned = true;
    root.innerHTML = '';
    document.title = previousTitle;
    window.removeEventListener('afterprint', cleanup);
  };
  window.addEventListener('afterprint', cleanup);
  // Some mobile browsers return from print() before the dialog closes and never fire afterprint.
  setTimeout(() => window.addEventListener('focus', cleanup, { once: true }), 1000);
  window.print();
}

/** Saves the document as a self-contained HTML file (opens in any browser, prints the same way). */
export function downloadPortalDocument(doc: PrintableDocument): void {
  const html = printDocument(doc.body, {
    title: doc.title,
    pageSize: 'A4',
    styles: `${BASE_STYLES.replaceAll(`#${ROOT_ID} `, '')}${doc.styles ?? ''}`,
  });
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = `${safeFileName(doc.fileName)}.html`;
  link.rel = 'noopener';
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}

/** Letters, digits, dash and underscore only (no path tricks, no personal data beyond what the caller chose). */
export function safeFileName(name: string): string {
  const cleaned = name
    .replace(/[^A-Za-z0-9_-]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');
  return cleaned.slice(0, 80) || 'document';
}

/** Prefixes each selector of a document's own styles with the print root, so they apply to the print copy only. */
export function scopeStyles(css: string): string {
  return css.replace(/(^|\})\s*([^{}@]+)\{/g, (_, close: string, selectors: string) => {
    const scoped = selectors
      .split(',')
      .map((selector) => `#${ROOT_ID} ${selector.trim()}`)
      .join(', ');
    return `${close} ${scoped} {`;
  });
}

/** Escapes a value and turns line breaks into <br> (clinical text keeps its paragraphs). */
export function escapeMultiline(value: string | null | undefined): string {
  return escapeHtml(value ?? '').replace(/\r?\n/g, '<br/>');
}

/**
 * The only markup a template accepts that it did not build itself: a QR code SVG rendered by the page. Anything
 * that could run script or load content is refused (defence in depth; the SVG comes from qrcode.react).
 */
export function safeSvgMarkup(svg: string | null | undefined): string | null {
  const markup = svg?.trim();
  if (!markup || !/^<svg[\s>]/i.test(markup) || !/<\/svg>$/i.test(markup)) {
    return null;
  }
  return /<script|<foreignobject|<iframe|<image|\son[a-z]+\s*=|javascript:|href\s*=/i.test(markup) ? null : markup;
}
