/**
 * Result tables of the printed lab report (pure HTML; unit-tested). Sections → tests → analytes with flag, unit and
 * reference interval; cultures with organism and antibiogram; amendments with reasons. Values that are not verified yet
 * are never printed as results.
 */
import { escapeHtml, formatDateTime, genderShort } from '@hc360/core';
import type { LabReportResultLine, LabReportTest, LabReportView } from './api';
import { flagDisplay } from './flags';
import { sectionLabel, specimenLabel } from './labels';

const interpretationWord: Record<string, string> = { Sensitive: 'Sensitive (S)', Intermediate: 'Intermediate (I)', Resistant: 'Resistant (R)' };

function flagCell(line: LabReportResultLine): string {
  const display = flagDisplay(line.flag);
  if (!display || !display.code) {
    return '';
  }
  return `<span class="${display.critical ? 'crit' : 'flag'}" title="${escapeHtml(display.label)}">${escapeHtml(display.code)}</span>`;
}

function valueCell(line: LabReportResultLine, releasedOnly: boolean): string {
  if (line.status !== 'Verified' && (releasedOnly || line.status === 'Pending')) {
    return '<span class="small">Awaited</span>';
  }
  const display = flagDisplay(line.flag);
  const css = display?.critical ? 'crit' : display && display.code ? 'abn' : '';
  const provisional = line.status !== 'Verified' ? ' <span class="small">(not verified)</span>' : '';
  const star = line.autoValidated ? '*' : '';
  return `<span class="${css}">${escapeHtml(line.value ?? '')}${star}</span>${provisional}`;
}

function cultureHtml(line: LabReportResultLine): string {
  const parts: string[] = [];
  if (line.organism) {
    parts.push(`<div>Organism isolated: <strong>${escapeHtml(line.organism)}</strong>${line.colonyCount ? ` · Colony count ${escapeHtml(line.colonyCount)}` : ''}</div>`);
  }
  if (line.susceptibilities.length > 0) {
    parts.push(`<table class="abg"><thead><tr><th>Antibiotic</th><th>Interpretation</th><th>MIC</th></tr></thead><tbody>${line.susceptibilities
      .map(
        (s) =>
          `<tr><td>${escapeHtml(s.antibiotic)}</td><td class="${s.interpretation === 'Resistant' ? 'abn' : ''}">${escapeHtml(interpretationWord[s.interpretation] ?? s.interpretation)}</td><td>${escapeHtml(s.mic ?? '')}</td></tr>`,
      )
      .join('')}</tbody></table>`);
  }
  return parts.join('');
}

function testHtml(test: LabReportTest, releasedOnly: boolean): string {
  const meta = [specimenLabel[test.specimenType], test.method ? `Method: ${test.method}` : null, test.referralLabName ? `Tested at ${test.referralLabName}` : null]
    .filter(Boolean)
    .join(' · ');
  let lastGroup: string | null = null;
  const rows = test.results
    .map((line) => {
      const groupRow = line.groupName && line.groupName !== lastGroup ? `<tr><td colspan="5"><strong>${escapeHtml(line.groupName)}</strong></td></tr>` : '';
      lastGroup = line.groupName;
      // Organism, antibiogram and remarks are part of the result: never shown before verification on a released copy.
      const printable = line.status === 'Verified' || (!releasedOnly && line.status !== 'Pending');
      const culture = printable ? cultureHtml(line) : '';
      const remarks = printable && line.remarks ? `<div class="small">${escapeHtml(line.remarks)}</div>` : '';
      return `${groupRow}<tr>
        <td>${escapeHtml(line.name)}</td>
        <td>${valueCell(line, releasedOnly)}${remarks}</td>
        <td>${line.status === 'Verified' || !releasedOnly ? flagCell(line) : ''}</td>
        <td>${escapeHtml(line.unit ?? '')}</td>
        <td>${escapeHtml(line.referenceText ?? '')}</td>
      </tr>${culture ? `<tr><td></td><td colspan="4">${culture}</td></tr>` : ''}`;
    })
    .join('');
  const status = test.status === 'Released' ? '' : ` <span class="small">(${test.status === 'Verified' ? 'verified, not yet released' : 'in progress'})</span>`;
  return `
    <h3>${escapeHtml(test.name)}${status}</h3>
    ${meta ? `<div class="small">${escapeHtml(meta)}</div>` : ''}
    <table>
      <thead><tr><th style="width:34%">Investigation</th><th style="width:22%">Result</th><th style="width:7%">Flag</th><th style="width:12%">Unit</th><th>Reference interval</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>
    ${test.interpretation ? `<div class="small"><strong>Interpretation:</strong> ${escapeHtml(test.interpretation)}</div>` : ''}`;
}

/** Result section of the printed report. `releasedOnly` prints only verified values (patient/clinician copies). */
export function labReportBodyHtml(view: LabReportView, releasedOnly = false): string {
  const sections = view.sections
    .map((section) => `<h2>${escapeHtml(sectionLabel[section.section])}</h2>${section.tests.map((test) => testHtml(test, releasedOnly)).join('')}`)
    .join('');
  const amendments = view.amendments.length
    ? `<h2>Amendments</h2><table><thead><tr><th>Revision</th><th>When</th><th>By</th><th>Reason and changes</th></tr></thead><tbody>${view.amendments
        .map(
          (a) =>
            `<tr><td>${a.revision}</td><td>${escapeHtml(formatDateTime(a.amendedAt))}</td><td>${escapeHtml(a.amendedBy ?? '')}</td><td>${escapeHtml(a.reason)}<div class="small">${escapeHtml(a.changes)}</div></td></tr>`,
        )
        .join('')}</tbody></table>`
    : '';
  return sections + amendments;
}

/** Patient block rows: identity, referral and the key times. */
export function labReportPatientRows(view: LabReportView): [string, string][] {
  const collected = view.sections
    .flatMap((s) => s.tests.map((t) => t.collectedAt))
    .filter((v): v is string => Boolean(v))
    .sort()[0];
  const received = view.sections
    .flatMap((s) => s.tests.map((t) => t.receivedAt))
    .filter((v): v is string => Boolean(v))
    .sort()[0];
  return [
    ['Patient', view.patient.name],
    ['Age / sex', `${view.patient.age ?? '—'} / ${genderShort[view.patient.gender]}`],
    ['UHID', view.patient.uhid],
    ['Report no.', view.revision > 1 ? `${view.reportNumber} (revision ${view.revision})` : view.reportNumber],
    ['Referred by', view.referredBy ?? 'Self'],
    ['Ordered', formatDateTime(view.orderedAt)],
    ['Collected', collected ? formatDateTime(collected) : '—'],
    ['Received', received ? formatDateTime(received) : '—'],
    ['Released', view.releasedAt ? formatDateTime(view.releasedAt) : 'Not released'],
  ];
}

/** Footnotes: partial report, auto-validation and amendment notices. */
export function labReportNotes(view: LabReportView): string[] {
  const notes: string[] = [];
  if (view.isPartial) {
    notes.push('Partial report: some tests are still in progress and will follow in an updated report.');
  }
  if (view.hasAutoValidatedResults) {
    notes.push('* Verified automatically under the laboratory auto-validation rules (in range, QC accepted, delta check passed).');
  }
  if (view.status === 'Amended') {
    notes.push('This report was amended after release. The amendments and their reasons are listed above.');
  }
  return notes;
}
