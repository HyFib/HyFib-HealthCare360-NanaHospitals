/** Printed radiology report body (pure HTML; unit-tested). Every value is escaped; free text keeps its paragraphs. */
import { escapeHtml, formatDateTime, genderShort } from '@hc360/core';
import { paragraphs } from '@/modules/lab/common/reportHtml';
import type { RadiologyReportView } from './api';
import { modalityLabel } from './labels';

function block(title: string, text: string | null | undefined, strong = false): string {
  const html = paragraphs(text);
  return html ? `<h3>${escapeHtml(title)}</h3><div class="${strong ? 'impression' : ''}">${html}</div>` : '';
}

export function radiologyReportBodyHtml(view: RadiologyReportView): string {
  const addenda = view.addenda.length
    ? `<h2>Addenda</h2>${view.addenda
        .map(
          (addendum) =>
            `<div class="addendum"><div class="small">${escapeHtml(formatDateTime(addendum.addedAt))}${addendum.addedBy ? ` · ${escapeHtml(addendum.addedBy)}` : ''}${addendum.reason ? ` · ${escapeHtml(addendum.reason)}` : ''}${addendum.isCriticalFinding ? ' · <span class="crit">critical finding</span>' : ''}</div>${paragraphs(addendum.text)}</div>`,
        )
        .join('')}`
    : '';
  return `
    <h2>${escapeHtml(view.studyName)}</h2>
    <div class="small">${escapeHtml(modalityLabel[view.modality])}${view.bodyPart ? ` · ${escapeHtml(view.bodyPart)}` : ''}</div>
    ${view.isCriticalFinding ? '<p class="crit">Critical finding: communicated to the treating doctor.</p>' : ''}
    ${block('Clinical history', view.clinicalHistory)}
    ${block('Technique', view.technique)}
    ${block('Comparison', view.comparison)}
    ${block('Findings', view.findings)}
    ${block('Impression', view.impression, true)}
    ${addenda}`;
}

export function radiologyPatientRows(view: RadiologyReportView): [string, string][] {
  return [
    ['Patient', view.patient.name],
    ['Age / sex', `${view.patient.age ?? '—'} / ${genderShort[view.patient.gender]}`],
    ['UHID', view.patient.uhid],
    ['Accession no.', view.accessionNumber],
    ['Referred by', view.referredBy ?? 'Self'],
    ['Ordered', formatDateTime(view.orderedAt)],
    ['Scanned', view.acquiredAt ? formatDateTime(view.acquiredAt) : '—'],
    [
      'Reported',
      view.releasedAt
        ? formatDateTime(view.releasedAt)
        : view.verifiedAt
          ? formatDateTime(view.verifiedAt)
          : 'Not signed',
    ],
  ];
}

export const RADIOLOGY_PRINT_STYLES = `
  .impression { font-weight: 600; }
  .addendum { border-left: 3px solid #999; padding-left: 8px; margin-bottom: 6px; }
`;
