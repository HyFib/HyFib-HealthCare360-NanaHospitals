import { escapeHtml, formatDateTime, printDocument, printHtml } from '@hc360/core';
import type { ScorecardRow } from './api';
import { formatIndicatorValue } from './rules';

/** A4 landscape scorecard for the quality committee: one table per category, every value escaped. */
export function scorecardHtml(rows: ScorecardRow[], facilityName: string, periodTitle: string, printedAt = new Date().toISOString()): string {
  const categories = [...new Set(rows.map((r) => r.category))];
  const met = rows.filter((r) => r.meetsTarget === true).length;
  const missed = rows.filter((r) => r.meetsTarget === false).length;
  const noData = rows.filter((r) => r.value === null).length;
  const tables = categories
    .map((category) => {
      const body = rows
        .filter((r) => r.category === category)
        .map((r) => {
          const status = r.value === null ? 'No data' : r.meetsTarget === null ? 'No target' : r.meetsTarget ? 'Met' : 'Missed';
          const target = r.target === null ? '—' : `${r.direction === 'HigherIsBetter' ? '≥' : '≤'} ${formatIndicatorValue(r.target, r.unit)}`;
          return `<tr><td>${escapeHtml(r.code)}</td><td>${escapeHtml(r.name)}</td><td>${escapeHtml(r.periodLabel)}</td><td class="right">${escapeHtml(
            formatIndicatorValue(r.value, r.unit),
          )}</td><td class="right">${escapeHtml(target)}</td><td class="status ${status === 'Missed' ? 'bad' : status === 'Met' ? 'good' : ''}">${escapeHtml(status)}</td></tr>`;
        })
        .join('');
      return `<h3>${escapeHtml(category)}</h3><table><thead><tr><th>Code</th><th>Indicator</th><th>Period</th><th class="right">Value</th><th class="right">Target</th><th>Status</th></tr></thead><tbody>${body}</tbody></table>`;
    })
    .join('');
  return `
  <div class="head">
    <div><div class="hospital">${escapeHtml(facilityName)}</div><div>Quality indicator scorecard · ${escapeHtml(periodTitle)}</div></div>
    <div class="summary">${met} met · ${missed} missed · ${noData} without data</div>
  </div>
  ${tables}
  <div class="foot">Printed ${escapeHtml(formatDateTime(printedAt))} from HealthCare360.</div>`;
}

const STYLES = `
  .head { display: flex; justify-content: space-between; align-items: flex-end; border-bottom: 2px solid #045E80; padding-bottom: 6px; margin-bottom: 8px; }
  .hospital { font-size: 15px; font-weight: 700; color: #045E80; }
  .summary { font-weight: 600; }
  h3 { margin: 12px 0 4px; font-size: 12px; }
  table { border: 1px solid #bbb; margin-bottom: 6px; }
  thead th { background: #eef5f9; border-bottom: 1px solid #bbb; }
  tbody tr { border-bottom: 1px solid #e3e3e3; }
  .status.bad { color: #a4262c; font-weight: 700; }
  .status.good { color: #107c10; font-weight: 600; }
  .foot { margin-top: 12px; color: #555; font-size: 9.5px; }`;

export async function printScorecard(rows: ScorecardRow[], facilityName: string, periodTitle: string): Promise<void> {
  const html = printDocument(scorecardHtml(rows, facilityName, periodTitle), { title: `Quality scorecard ${periodTitle}`, pageSize: 'A4', styles: STYLES });
  await printHtml(html, { pageSize: 'A4', landscape: true });
}
