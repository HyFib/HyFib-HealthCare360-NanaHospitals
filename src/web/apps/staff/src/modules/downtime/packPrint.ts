import { escapeHtml, formatDateTime, printDocument, printHtml } from '@hc360/core';
import type { DowntimeNumberBlockDetail, DowntimePack } from './continuityApi';

/**
 * The pre-downtime pack as an A4 document: a cover with what it holds and from when, then each section's tables. A
 * medication sheet starts its own page, to go to the bedside. Every page says when the pack was made, so a nurse can tell
 * an old copy from the current one.
 */
export function packHtml(pack: DowntimePack): string {
  const made = `${formatDateTime(pack.generatedAt)}${pack.generatedByName ? ` by ${escapeHtml(pack.generatedByName)}` : ' (nightly)'}`;
  const contents = pack.sections
    .map((s) => `<li>${escapeHtml(s.title)} (${s.tables.length} ${s.tables.length === 1 ? 'table' : 'tables'})</li>`)
    .join('');
  const sections = pack.sections
    .map((section) => {
      const tables = section.tables
        .map((table, index) => {
          const head = table.columns.map((c) => `<th scope="col">${escapeHtml(c)}</th>`).join('');
          const body =
            table.rows.length === 0
              ? `<tr><td colspan="${table.columns.length}">None.</td></tr>`
              : table.rows
                  .map((row) => `<tr>${row.map((cell) => `<td>${escapeHtml(cell)}</td>`).join('')}</tr>`)
                  .join('');
          const page = section.pagePerTable && index > 0 ? ' page' : '';
          return `
        <div class="table${page}">
          <h3>${escapeHtml(table.heading)}</h3>
          ${table.alert ? `<p class="alert">${escapeHtml(table.alert)}</p>` : ''}
          ${table.note ? `<p class="note">${escapeHtml(table.note)}</p>` : ''}
          <table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>
        </div>`;
        })
        .join('');
      return `
      <section class="page">
        <h2>${escapeHtml(section.title)}</h2>
        ${section.note ? `<p class="note">${escapeHtml(section.note)}</p>` : ''}
        ${tables || '<p>Nothing to print.</p>'}
      </section>`;
    })
    .join('');
  return `
    <header>
      <h1>${escapeHtml(pack.facilityName)}: pre-downtime pack</h1>
      <p class="meta">Made ${made} · covers until ${escapeHtml(formatDateTime(pack.windowEndsAt))} · ${pack.patients} in-patients</p>
      <p class="note">Use while HC360 cannot be reached. Anything ordered or changed after the time above is on paper only.</p>
      <ol>${contents}</ol>
    </header>
    ${sections}`;
}

const STYLES = `
  h1 { font-size: 14pt; margin: 0; }
  h2 { font-size: 12pt; margin: 0 0 4px; }
  h3 { font-size: 10pt; margin: 8px 0 2px; }
  .meta { font-size: 9pt; color: #333; margin: 2px 0; }
  .note { font-size: 8.5pt; margin: 2px 0 4px; }
  .alert { font-size: 9pt; font-weight: 700; margin: 2px 0; }
  .page { page-break-before: always; }
  table { width: 100%; border-collapse: collapse; font-size: 8.5pt; }
  th, td { border: 1px solid #888; padding: 2px 4px; vertical-align: top; text-align: left; }
  th { background: #eee; }
  tr { page-break-inside: avoid; }`;

export async function printPack(pack: DowntimePack): Promise<void> {
  await printHtml(
    printDocument(packHtml(pack), {
      title: `Pre-downtime pack ${formatDateTime(pack.generatedAt)}`,
      pageSize: 'A4',
      styles: STYLES,
    }),
    { pageSize: 'A4' },
  );
}

/**
 * A counter's downtime number sheet (A4): each number of the block in order with lines for the patient, the time and
 * the amount, and who entered it when HC360 is back, so the paper can be matched to the block.
 */
export function numberSheetHtml(detail: DowntimeNumberBlockDetail): string {
  const rows = detail.numbers
    .map(
      (n) =>
        `<tr><td class="pos">${n.position}</td><td class="num">${escapeHtml(n.number)}</td><td></td><td></td><td></td><td></td></tr>`,
    )
    .join('');
  return `
    <h1>${escapeHtml(detail.facilityName)}: downtime ${escapeHtml(detail.block.kindName)} numbers</h1>
    <p class="meta">${escapeHtml(detail.block.counter)} · issued ${escapeHtml(formatDateTime(detail.block.issuedAt))} · ${detail.numbers.length} numbers</p>
    <p class="note">Use these only while HC360 cannot be reached, in order, one per paper ${escapeHtml(detail.block.kindName)}. Write the date and
    time on every paper. Cross out a spoilt number and write "void". When HC360 is back, enter each paper with its number and
    the time on it; return this sheet to medical records with the block.</p>
    <table>
      <thead><tr><th scope="col">#</th><th scope="col">Number</th><th scope="col">Patient (UHID)</th><th scope="col">Date and time</th><th scope="col">Amount</th><th scope="col">Entered in HC360 (initials)</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>`;
}

export async function printNumberSheet(detail: DowntimeNumberBlockDetail): Promise<void> {
  await printHtml(
    printDocument(numberSheetHtml(detail), {
      title: `Downtime numbers ${detail.block.counter}`,
      pageSize: 'A4',
      styles: `${STYLES} td.pos { width: 8mm; text-align: right; } td.num { font-family: Consolas, monospace; width: 40mm; } td { height: 7mm; }`,
    }),
    { pageSize: 'A4' },
  );
}
