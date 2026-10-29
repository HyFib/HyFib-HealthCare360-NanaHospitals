import { escapeHtml } from '@hc360/core';
import { LOCALES, type Language } from '@/i18n';

/**
 * Printing and saving documents (reports, prescriptions, bills, receipts, discharge summaries, the privacy summary).
 *
 * The portal prints in the page itself rather than through a hidden iframe: iPhone Safari prints the whole page
 * instead of an iframe, and the in-page copy works the same on Android, iPhone and computers. The copy is in the
 * patient's language. Reports, prescriptions, discharge summaries and bills also have the hospital's official PDF,
 * made by the server (see `pdf.ts`). For the other documents the print dialog's "Save as PDF" gives a PDF, and
 * "Download a copy" saves the same document as a self-contained HTML file.
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

/**
 * The base styles of every document. `root` is the element that holds the document: the print root in the page
 * (so the styles can never affect the app on screen), or `body` in a downloaded copy.
 */
function baseStyles(root: string): string {
  return `
@page { size: A4; margin: 12mm; }
${root} { font-family: "Segoe UI", "Noto Sans", "Noto Sans Devanagari", "Noto Sans Tamil", "Nirmala UI", system-ui, sans-serif; color: #111; font-size: 11pt; line-height: 1.4; }
${root} h1 { font-size: 16pt; margin: 0 0 4px; }
${root} h2 { font-size: 12.5pt; margin: 14px 0 6px; }
${root} h3 { font-size: 11.5pt; margin: 10px 0 4px; }
${root} table { width: 100%; border-collapse: collapse; }
${root} th, ${root} td { text-align: left; padding: 4px 6px; vertical-align: top; border-bottom: 1px solid #ddd; }
${root} th { font-size: 9.5pt; color: #444; }
${root} .muted { color: #555; }
${root} .right { text-align: right; }
${root} .small { font-size: 9pt; }
${root} .rule { border-top: 1px solid #999; margin: 8px 0; }
${root} .avoid-break { break-inside: avoid; }
`;
}

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
  // The window and the page are held from here on, not looked up again: the fallback below runs a second later, when
  // the window may be gone (in tests, once the file has ended).
  const win = window;
  const page = win.document;
  const root = ensureRoot();
  // eslint-disable-next-line no-unsanitized/property -- the modules' templates escape every value, and the styles are their constants
  root.innerHTML = `<style>@media print {${baseStyles(`#${ROOT_ID}`)}${scopeStyles(doc.styles ?? '')}}</style>${doc.body}`;
  const previousTitle = page.title;
  page.title = doc.title;
  let cleaned = false;
  const cleanup = () => {
    if (cleaned) {
      return;
    }
    cleaned = true;
    clearTimeout(fallback);
    root.innerHTML = '';
    page.title = previousTitle;
    win.removeEventListener('afterprint', cleanup);
    win.removeEventListener('focus', cleanup);
  };
  win.addEventListener('afterprint', cleanup);
  // Some mobile browsers return from print() before the dialog closes and never fire afterprint.
  const fallback = setTimeout(() => win.addEventListener('focus', cleanup, { once: true }), 1000);
  win.print();
}

/**
 * The document as a page of its own, marked with the language it was written in (screen readers pick the right
 * voice, browsers the right font). It opens in any browser, phones included, and prints like the in-page copy.
 */
export function standaloneHtml(doc: PrintableDocument, language: Language): string {
  return `<!doctype html>
<html lang="${LOCALES[language]}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(doc.title)}</title>
<style>
body { margin: 16px; }
@media print { body { margin: 0; } }
${baseStyles('body')}${doc.styles ?? ''}
</style>
</head>
<body>${doc.body}</body>
</html>`;
}

/** Saves the document as a self-contained HTML file in the patient's language ("Download a copy"). */
export function downloadPortalDocument(doc: PrintableDocument, language: Language): void {
  const html = standaloneHtml(doc, language);
  saveBlob(new Blob([html], { type: 'text/html;charset=utf-8' }), `${safeFileName(doc.fileName)}.html`);
}

/**
 * Hands a file to the browser's downloads: Android Chrome saves it at once, iPhone Safari first asks whether to
 * download it. The file's address stays valid for a minute, so a browser that asks first can still read it.
 */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
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
