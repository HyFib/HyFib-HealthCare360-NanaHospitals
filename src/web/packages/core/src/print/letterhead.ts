import { escapeHtml } from './escape';

/**
 * The letterhead and print templates of the documents the apps print themselves (SRS HMS-04, UX-09): 80 mm bills and
 * receipts, pharmacy, canteen and store slips, reports for the office. An administrator sets the templates in Hospital
 * setup › Print templates, hospital-wide or for one facility, and the server's own documents (A4 bills, reports,
 * prescriptions) follow the same ones. The staff app reads both from `GET /api/v1/print/letterhead`.
 */

/** The kinds of document a template is set for, as the server's `PrintTemplateCatalog` names them. */
export const PRINT_TEMPLATE_KEYS = [
  'bill',
  'receipt',
  'sales-invoice',
  'report',
  'prescription',
  'clinical-note',
  'purchase',
  'other',
] as const;

export type PrintTemplateKey = (typeof PRINT_TEMPLATE_KEYS)[number];

/** How one kind of document is printed. */
export interface PrintTemplate {
  /** `Preprinted`: the paper is the hospital's own stationery, so the letterhead is left off and room is kept for it. */
  letterhead: 'Printed' | 'Preprinted';
  /** Blank space at the top of every A4 or A5 page for the stationery's letterhead, in millimetres. Pre-printed paper only. */
  topSpaceMm: number;
  /** Blank space at the foot of every A4 or A5 page for the stationery's footer, in millimetres. Pre-printed paper only. */
  bottomSpaceMm: number;
  showLogo: boolean;
  /** Plain text printed at the end of the document, such as terms or a thank-you line. Line breaks are kept. */
  closingNote: string | null;
  /** The paper, where the document offers a choice (prescriptions); null for its usual one. */
  paper: 'A4' | 'A5' | null;
}

/** The template of a document nobody has set up: the letterhead printed with the logo, and nothing added. */
export const DEFAULT_PRINT_TEMPLATE: Readonly<PrintTemplate> = Object.freeze({
  letterhead: 'Printed',
  topSpaceMm: 0,
  bottomSpaceMm: 0,
  showLogo: true,
  closingNote: null,
  paper: null,
});

/** The most blank space a template keeps at the top or foot of a page. */
export const MAX_PRINT_SPACE_MM = 80;

/** The longest closing note a template may have. */
export const MAX_CLOSING_NOTE_LENGTH = 500;

/**
 * The hospital as printed at the top of a document: the facility's letterhead, its logo ready to embed, and the
 * templates of the user's facility. Only the name is needed: a print made before the rest arrives shows the name alone.
 */
export interface Letterhead {
  name: string;
  legalName?: string | null;
  /** One line: "12 MG Road, Pune, Maharashtra, 411001". */
  address?: string | null;
  phone?: string | null;
  email?: string | null;
  website?: string | null;
  registrationNumber?: string | null;
  nabhNumber?: string | null;
  gstin?: string | null;
  pan?: string | null;
  /** The facility's own letterhead line, such as accreditation text. */
  note?: string | null;
  /** The logo as a data: URI. */
  logo?: string | null;
  templates?: Partial<Record<PrintTemplateKey, PrintTemplate>>;
}

/** The template `key` prints with at the user's facility; the defaults until the templates arrive. */
export function templateFor(letterhead: Pick<Letterhead, 'templates'>, key: PrintTemplateKey): PrintTemplate {
  return letterhead.templates?.[key] ?? DEFAULT_PRINT_TEMPLATE;
}

export interface LetterheadOptions {
  /** On an 80 mm roll: centred and small. A roll is never pre-printed, so its letterhead is always printed. */
  compact?: boolean;
  /** Adds GSTIN and PAN, which a tax invoice, receipt or voucher shows whatever the paper has on it. */
  taxIds?: boolean;
  /** Adds the hospital's registration number. */
  registration?: boolean;
  /** HTML for the right-hand side, already escaped: the document's title and numbers. Not used when compact. */
  aside?: string;
}

const IMAGE_DATA_URI = /^data:image\/(png|jpeg|webp|svg\+xml);base64,[A-Za-z0-9+/]+={0,2}$/;

/**
 * The letterhead at the top of a document, as the server's documents print it: logo, name, address, "Phone · e-mail",
 * the tax ids and registration when asked for, NABH accreditation and the facility's own line. On pre-printed
 * stationery only the tax ids and the aside are printed: the paper has the rest.
 */
export function letterheadHtml(
  letterhead: Letterhead,
  template: PrintTemplate,
  options: LetterheadOptions = {},
): string {
  const e = escapeHtml;
  const tax = options.taxIds
    ? [letterhead.gstin ? `GSTIN ${letterhead.gstin}` : '', letterhead.pan ? `PAN ${letterhead.pan}` : '']
        .filter(Boolean)
        .join(' · ')
    : '';
  const aside = !options.compact && options.aside ? `<div class="lh-aside">${options.aside}</div>` : '';
  if (template.letterhead === 'Preprinted' && !options.compact) {
    const left = tax ? `<div class="lh-text"><div class="lh-line">${e(tax)}</div></div>` : '';
    return left || aside ? `<header class="lh preprinted">${left}${aside}</header>` : '';
  }

  const contact = [
    letterhead.phone ? `Phone ${letterhead.phone}` : '',
    letterhead.email ?? '',
    options.compact ? '' : (letterhead.website ?? ''),
  ]
    .filter(Boolean)
    .join(' · ');
  const lines = [
    letterhead.address,
    contact,
    tax,
    options.registration && letterhead.registrationNumber ? `Registration ${letterhead.registrationNumber}` : '',
    letterhead.nabhNumber ? `NABH accreditation ${letterhead.nabhNumber}` : '',
    letterhead.note,
  ]
    .filter((line): line is string => Boolean(line?.trim()))
    .map((line) => `<div class="lh-line">${e(line)}</div>`)
    .join('');
  const logo =
    template.showLogo && letterhead.logo && IMAGE_DATA_URI.test(letterhead.logo)
      ? `<img class="lh-logo" src="${e(letterhead.logo)}" alt="">`
      : '';
  return `<header class="lh${options.compact ? ' compact' : ''}">${logo}<div class="lh-text"><div class="lh-name">${e(letterhead.legalName || letterhead.name)}</div>${lines}</div>${aside}</header>`;
}

/** The template's closing note with its line breaks; empty when it has none. */
export function closingNoteHtml(template: PrintTemplate | undefined): string {
  const note = template?.closingNote?.trim();
  return note
    ? `<div class="closing-note">${note
        .split(/\r?\n/)
        .map((line) => escapeHtml(line))
        .join('<br>')}</div>`
    : '';
}

/** Room for pre-printed stationery: the page margin with the template's blank space added (A4 and A5 only). */
export function stationeryMarginMm(template: PrintTemplate | undefined): { top: number; bottom: number } {
  if (template?.letterhead !== 'Preprinted') {
    return { top: 0, bottom: 0 };
  }
  return { top: spaceMm(template.topSpaceMm), bottom: spaceMm(template.bottomSpaceMm) };
}

function spaceMm(value: number): number {
  return Number.isFinite(value) ? Math.min(Math.max(Math.round(value), 0), MAX_PRINT_SPACE_MM) : 0;
}

/** The letterhead's and closing note's styles; every page `printDocument` makes has them. */
export const LETTERHEAD_STYLES = `
.lh { display: flex; gap: 10px; align-items: center; border-bottom: 2px solid #045E80; padding-bottom: 6px; margin-bottom: 8px; }
.lh .lh-logo { max-height: 18mm; max-width: 40mm; object-fit: contain; }
.lh .lh-text { flex: 1 1 auto; min-width: 0; }
.lh .lh-name { font-size: 15px; font-weight: 700; color: #045E80; }
.lh .lh-line { color: #555; }
.lh .lh-aside { flex: 0 0 auto; max-width: 55%; margin-left: auto; text-align: right; }
.lh.preprinted { border-bottom: none; padding-bottom: 0; }
.lh.compact { display: block; text-align: center; border-bottom: none; padding-bottom: 0; margin-bottom: 2px; }
.lh.compact .lh-logo { display: block; margin: 0 auto 2px; max-height: 12mm; max-width: 40mm; filter: grayscale(1); }
.lh.compact .lh-name { font-size: 12.5px; color: #000; }
.lh.compact .lh-line { font-size: 9.5px; color: #000; }
.closing-note { margin-top: 10px; font-size: 0.85em; color: #333; break-inside: avoid; }`;
