import { escapeHtml } from '@hc360/core';
import type { Translate } from '@/i18n';
import { istDateOf } from '@/i18n/format';
import type { Formatters } from '@/i18n/useFormat';
import { escapeMultiline, safeFileName, type PrintableDocument } from '@/lib/print';
import type { DischargeSummaryDto, PortalProfile } from '@/lib/types';

export interface DischargePrintContext {
  t: Translate;
  f: Formatters;
  hospital: string;
  printedAt: string;
  patient: Pick<PortalProfile, 'fullName' | 'uhid'>;
}

const STYLES = `
.head { display: flex; justify-content: space-between; gap: 12px; border-bottom: 2px solid #0A7CA7; padding-bottom: 6px; margin-bottom: 8px; }
.facility { font-size: 14pt; font-weight: 700; color: #045E80; }
.patient { display: flex; flex-wrap: wrap; gap: 4px 18px; margin: 6px 0 10px; }
.section { break-inside: avoid; }
.section p { margin: 0; }
.footer { margin-top: 16px; font-size: 8.5pt; color: #555; border-top: 1px solid #ccc; padding-top: 4px; }
`;

/** The patient's copy of a signed discharge summary, section by section as the doctor wrote it. */
export function dischargeSummaryDocument(summary: DischargeSummaryDto, ctx: DischargePrintContext): PrintableDocument {
  const { t, f } = ctx;
  const section = (title: string, text: string | null | undefined) =>
    text?.trim()
      ? `<div class="section"><h2>${escapeHtml(title)}</h2><p>${escapeMultiline(text.trim())}</p></div>`
      : '';
  const codes = summary.diagnosisCodes.length
    ? ` <span class="small muted">(${escapeHtml(summary.diagnosisCodes.join(', '))})</span>`
    : '';
  const followUp = [
    summary.followUpDate ? t('discharge.followUpOn', { date: f.calendarDate(summary.followUpDate) }) : '',
    summary.followUpInstructions ?? '',
  ]
    .filter((part) => part.trim())
    .join('\n');
  const stay = [
    summary.admittedAt ? t('discharge.admitted', { date: f.date(summary.admittedAt) }) : '',
    summary.dischargedAt ? t('discharge.discharged', { date: f.date(summary.dischargedAt) }) : '',
  ]
    .filter(Boolean)
    .join(' · ');
  const signed = [
    summary.signedAt ? t('discharge.signed', { date: f.dateTime(summary.signedAt) }) : '',
    summary.amendedAt ? t('discharge.amended', { date: f.dateTime(summary.amendedAt) }) : '',
  ]
    .filter(Boolean)
    .join(' · ');

  const body = `<div class="head"><div class="facility">${escapeHtml(ctx.hospital)}</div><div class="right"><h1>${escapeHtml(t('discharge.title'))}</h1><div class="small">${escapeHtml(
    stay,
  )}</div></div></div>
<div class="patient"><span><strong>${escapeHtml(ctx.patient.fullName)}</strong></span><span>${escapeHtml(t('common.uhid'))} ${escapeHtml(ctx.patient.uhid)}</span>${
    summary.practitionerName
      ? `<span>${escapeHtml(t('discharge.doctor'))}: ${escapeHtml(summary.practitionerName)}</span>`
      : ''
  }</div>
${summary.diagnoses?.trim() ? `<div class="section"><h2>${escapeHtml(t('discharge.diagnoses'))}</h2><p>${escapeMultiline(summary.diagnoses.trim())}${codes}</p></div>` : ''}
${section(t('discharge.complaints'), summary.presentingComplaints)}
${section(t('discharge.course'), summary.courseInHospital)}
${section(t('discharge.procedures'), summary.procedures)}
${section(t('discharge.investigations'), summary.investigationsSummary)}
${section(t('discharge.condition'), summary.conditionAtDischarge)}
${section(t('discharge.medications'), summary.medications)}
${section(t('discharge.advice'), summary.advice)}
${section(t('discharge.followUp'), followUp)}
<div class="footer">${signed ? `${escapeHtml(signed)}<br/>` : ''}${escapeHtml(t('print.printedOn', { date: f.dateTime(ctx.printedAt) }))}</div>`;

  const day = summary.dischargedAt ? istDateOf(summary.dischargedAt) : '';
  return { title: t('discharge.title'), body, styles: STYLES, fileName: safeFileName(`discharge-summary-${day}`) };
}
