import { escapeHtml, formatDateOnly, formatDateTime, printDocument, printHtml, type CssdPackLabel } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { printLabelJob } from '@/lib/labelPrinting';
import type { LoadDto, SetIssueDto } from './api';
import { METHOD_LABEL } from './rules';

/** The packs of a load as the server writes their labels for a ZPL or TSPL label printer. */
export function packLabelData(load: LoadDto, packIds?: readonly string[]): CssdPackLabel[] {
  const packs = packIds ? load.packs.filter((p) => packIds.includes(p.id)) : load.packs;
  return packs.map((pack) => ({
    packNumber: pack.packNumber,
    setName: pack.setName ?? 'Instrument set',
    method: METHOD_LABEL[load.method],
    sterileUntil: pack.sterileUntil,
  }));
}

/**
 * One 50 × 25 mm label per pack: set name, pack number as Code 128 (scanned at issue and use), the method and the
 * sterile-until date once the load is released. Printed without a dialog on this computer's label printer.
 */
export async function printPackLabels(load: LoadDto, packIds?: readonly string[]): Promise<void> {
  const labels = packLabelData(load, packIds);
  if (labels.length === 0) {
    return;
  }
  await printLabelJob({ kind: 'cssd-packs', labels, page: () => packLabelsPage(load, packIds) });
}

/** The pack labels as a page, for label printers on their Windows driver and for browsers. */
async function packLabelsPage(load: LoadDto, packIds?: readonly string[]): Promise<string> {
  const packs = packIds ? load.packs.filter((p) => packIds.includes(p.id)) : load.packs;
  const labels = await Promise.all(
    packs.map(async (pack) => {
      const code = await barcodeSvg(pack.packNumber, 'code128', { height: 7 });
      return `
      <div class="label">
        <div class="set">${escapeHtml(pack.setName ?? 'Instrument set')}</div>
        <div class="code">${code}</div>
        <div class="number">${escapeHtml(pack.packNumber)}</div>
        <div class="meta">${escapeHtml(METHOD_LABEL[load.method])}${pack.sterileUntil ? ` · Use by ${escapeHtml(formatDateOnly(pack.sterileUntil))}` : ' · Not yet released'}</div>
      </div>`;
    }),
  );
  const styles = `
    body { font-size: 7pt; }
    .label { width: 46mm; height: 21mm; overflow: hidden; page-break-after: always; display: flex; flex-direction: column; }
    .label:last-child { page-break-after: auto; }
    .set { font-weight: 700; font-size: 7.5pt; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .code svg { width: 100%; height: 9mm; display: block; }
    .number { font-family: Consolas, monospace; font-size: 7pt; text-align: center; letter-spacing: 0.5px; }
    .meta { font-size: 6pt; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }`;
  return printDocument(labels.join(''), { title: `Pack labels ${load.cycleNumber}`, pageSize: 'Label50x25', styles });
}

/** A5 issue slip that travels with the packs to the department (signed on receipt). */
export async function printIssueSlip(issues: readonly SetIssueDto[], facilityName: string): Promise<void> {
  const first = issues[0];
  if (!first) {
    return;
  }
  const rows = issues
    .map(
      (i) =>
        `<tr><td>${escapeHtml(i.setName ?? '')}</td><td class="mono">${escapeHtml(i.packNumber ?? '')}</td><td class="mono">${escapeHtml(i.cycleNumber ?? '')}</td><td>${escapeHtml(
          i.sterileUntil ? formatDateOnly(i.sterileUntil) : '—',
        )}</td></tr>`,
    )
    .join('');
  const body = `
    <div class="hospital">${escapeHtml(facilityName)} · CSSD</div>
    <h1>Sterile pack issue</h1>
    <div>To: <strong>${escapeHtml(first.departmentName ?? '')}</strong>${first.issuedToName ? ` · received by ${escapeHtml(first.issuedToName)}` : ''}</div>
    <div class="muted">${escapeHtml(formatDateTime(first.issuedAt))}</div>
    <div class="rule"></div>
    <table><thead><tr><th>Set</th><th>Pack</th><th>Cycle</th><th>Use by</th></tr></thead><tbody>${rows}</tbody></table>
    <div class="signs"><span>Issued by (CSSD)</span><span>Received by</span></div>`;
  const styles = `
    .hospital { font-weight: 700; color: #045E80; }
    h1 { font-size: 13pt; }
    th { border-bottom: 1px solid #999; }
    td { border-bottom: 1px solid #ddd; }
    .mono { font-family: Consolas, monospace; }
    .signs { display: flex; justify-content: space-between; margin-top: 30px; font-size: 9pt; }
    .signs span { border-top: 1px solid #555; padding-top: 3px; min-width: 40%; text-align: center; }`;
  await printHtml(printDocument(body, { title: 'Sterile pack issue', pageSize: 'A5', styles }), { pageSize: 'A5' });
}
