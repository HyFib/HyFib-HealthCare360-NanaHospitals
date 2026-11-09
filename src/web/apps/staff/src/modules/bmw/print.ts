import { escapeHtml, formatDateOnly, printDocument, printHtml, type WasteBagLabel } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { printLabelJob } from '@/lib/labelPrinting';
import type { FormI, FormIv, FormLine, WasteBagLabelDto } from './api';

/** The bag's label as the server writes it for a ZPL or TSPL label printer. */
export function bagLabelData(label: WasteBagLabelDto): WasteBagLabel {
  return {
    barcode: label.barcode,
    category: label.category,
    symbol: label.symbol,
    hcfId: label.hcfId,
    hospital: label.hcfName,
    source: label.source,
    weightKg: label.weightKg,
    collectedOn: label.collectedOn,
  };
}

/**
 * The bag's label (BMW Rules 2016, Schedule IV): the category in white on black with its symbol, the HCF identifier, the
 * bar code, the weight and the date, and where it was filled. Printed on this computer's label printer, at least 75 × 50 mm.
 */
export async function printBagLabel(label: WasteBagLabelDto): Promise<void> {
  await printLabelJob({ kind: 'waste-bags', labels: [bagLabelData(label)], page: () => bagLabelPage(label) });
}

/** The label as a page, for label printers on their Windows driver and for browsers. */
async function bagLabelPage(label: WasteBagLabelDto): Promise<string> {
  const code = await barcodeSvg(label.barcode, 'code128', { height: 6 });
  const styles = `
    body { font-size: 6pt; }
    .label { width: 46mm; height: 21mm; overflow: hidden; display: flex; flex-direction: column; }
    .category { display: flex; gap: 1.5mm; align-items: center; }
    .colour { background: #000; color: #fff; font-weight: 700; padding: 0 1mm; font-size: 7pt; }
    .code svg { width: 100%; height: 6mm; display: block; }
    .number { text-align: center; font-weight: 700; }
    .weighed { font-weight: 700; font-size: 7pt; }
  `;
  const body = `
    <div class="label">
      <div class="category"><span class="colour">${escapeHtml(label.category.toUpperCase())}</span><span>${escapeHtml(label.symbol)}</span></div>
      <div>HCF ${escapeHtml(label.hcfId)}</div>
      <div class="code">${code}</div>
      <div class="number">${escapeHtml(label.barcode)}</div>
      <div class="weighed">${escapeHtml(label.weightKg.toString())} kg · ${escapeHtml(formatDateOnly(label.collectedOn))}</div>
      <div>${escapeHtml(label.source)} · ${escapeHtml(label.hcfName)}</div>
    </div>`;
  return printDocument(body, { title: `Waste bag ${label.barcode}`, pageSize: 'Label50x25', styles });
}

function formTable(lines: readonly FormLine[]): string {
  return `<table class="form"><tbody>${lines
    .map(
      (l) =>
        `<tr><td class="no">${escapeHtml(l.number)}</td><td class="item">${escapeHtml(l.item)}</td><td>${escapeHtml(l.answer)}</td></tr>`,
    )
    .join('')}</tbody></table>`;
}

const FORM_STYLES = `
  table.form { width: 100%; border-collapse: collapse; }
  table.form td { border: 1px solid #999; padding: 4px 6px; vertical-align: top; }
  td.no { width: 48px; } td.item { width: 40%; }
`;

/** Form I, the report of an accident, filled from the record, to sign and send to the prescribed authority. */
export function formIHtml(form: FormI): string {
  const body = `
    <h2>FORM – I</h2>
    <p>[See rule 4(o), 5(i) and 15(2)] Accident reporting</p>
    <p><strong>${escapeHtml(form.hcfName)}</strong>${form.address ? `, ${escapeHtml(form.address)}` : ''}${form.authorisationNumber ? ` · Authorisation ${escapeHtml(form.authorisationNumber)}` : ''}</p>
    ${formTable(form.lines)}`;
  return printDocument(body, { title: 'Form I: accident reporting', pageSize: 'A4', styles: FORM_STYLES });
}

/** Form IV, the annual report, filled from the year's records; what HC360 does not keep is left for the hospital to fill. */
export function formIvHtml(form: FormIv): string {
  const body = `
    <h2>FORM – IV</h2>
    <p>[See rule 13] Annual report for the calendar year ${form.year}, to the prescribed authority by 30 June</p>
    ${formTable(form.lines)}`;
  return printDocument(body, { title: `Form IV: annual report ${form.year}`, pageSize: 'A4', styles: FORM_STYLES });
}

export const printFormI = (form: FormI) => printHtml(formIHtml(form));

export const printFormIv = (form: FormIv) => printHtml(formIvHtml(form));
