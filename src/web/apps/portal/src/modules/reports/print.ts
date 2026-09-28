import { escapeHtml } from '@hc360/core';
import type { Translate } from '@/i18n';
import type { Formatters } from '@/i18n/useFormat';
import { GENDER_KEY } from '@/lib/people';
import { escapeMultiline, safeFileName, safeSvgMarkup, type PrintableDocument } from '@/lib/print';
import type {
  CardiologyReportView,
  LabReportResultLine,
  LabReportView,
  RadiologyReportView,
  ReportLetterhead,
  ReportPatient,
  ReportSignatory,
} from '@/lib/types';
import { cardiologyMeasurements } from './cardiology';
import { CARDIOLOGY_TYPE_KEY, flagInfo, MODALITY_KEY, SECTION_KEY, SUSCEPTIBILITY_KEY } from './labResults';

export interface PrintContext {
  t: Translate;
  f: Formatters;
  /** When the copy was printed (ISO). */
  printedAt: string;
  /** SVG markup of the verification QR code as rendered on the page (null when there is none). */
  qrSvg?: string | null;
}

const REPORT_STYLES = `
.letterhead { display: flex; justify-content: space-between; gap: 12px; border-bottom: 2px solid #0A7CA7; padding-bottom: 6px; margin-bottom: 8px; }
.facility { font-size: 14pt; font-weight: 700; color: #045E80; }
.patient { display: flex; flex-wrap: wrap; gap: 4px 18px; margin: 6px 0 10px; font-size: 10pt; }
.flag-1 { font-weight: 700; }
.flag-2 { font-weight: 700; text-decoration: underline; }
.box { border: 1px solid #0A7CA7; border-radius: 4px; padding: 6px 8px; margin: 6px 0; }
.sign { margin-top: 14px; }
.footer { margin-top: 16px; font-size: 8.5pt; color: #555; border-top: 1px solid #ccc; padding-top: 4px; }
.qr { display: flex; gap: 10px; align-items: center; margin-top: 12px; }
.qr svg { width: 24mm; height: 24mm; }
`;

function letterhead(head: ReportLetterhead, title: string): string {
  const contact = [head.address, head.phone, head.email].filter(Boolean).map((part) => escapeHtml(part)).join(' · ');
  return `<div class="letterhead"><div><div class="facility">${escapeHtml(head.facilityName)}</div>${
    contact ? `<div class="small muted">${contact}</div>` : ''
  }${head.nabhNumber ? `<div class="small muted">NABH ${escapeHtml(head.nabhNumber)}</div>` : ''}</div><div><h1>${escapeHtml(title)}</h1></div></div>`;
}

function patientLine(patient: ReportPatient, ctx: PrintContext, extra: [string, string | null | undefined][]): string {
  const { t } = ctx;
  const items: [string, string | null | undefined][] = [
    [t('print.patient'), patient.name],
    [t('common.uhid'), patient.uhid],
    [t('common.details'), [patient.age, patient.gender === 'Unknown' ? null : t(GENDER_KEY[patient.gender])].filter(Boolean).join(' · ')],
    ...extra,
  ];
  return `<div class="patient">${items
    .filter(([, value]) => value)
    .map(([label, value]) => `<span><span class="muted">${escapeHtml(label)}:</span> <strong>${escapeHtml(value)}</strong></span>`)
    .join('')}</div>`;
}

function signatories(list: ReportSignatory[], ctx: PrintContext): string {
  if (list.length === 0) {
    return '';
  }
  return `<div class="sign"><div class="muted small">${escapeHtml(ctx.t('reports.signedBy'))}</div>${list
    .map(
      (signer) =>
        `<div><strong>${escapeHtml(signer.name)}</strong>${
          signer.qualification || signer.registrationNumber
            ? ` <span class="small muted">${escapeHtml([signer.qualification, signer.registrationNumber].filter(Boolean).join(' · '))}</span>`
            : ''
        }</div>`,
    )
    .join('')}</div>`;
}

function footer(code: string | null, ctx: PrintContext): string {
  const qr = code ? safeSvgMarkup(ctx.qrSvg) : null;
  const check = code
    ? `<div class="qr avoid-break">${qr ?? ''}<div class="small">${escapeHtml(ctx.t('reports.verification', { code }))}<br/>${escapeHtml(ctx.t('reports.verificationHint'))}</div></div>`
    : '';
  return `${check}<div class="footer">${escapeHtml(ctx.t('print.printedOn', { date: ctx.f.dateTime(ctx.printedAt) }))}</div>`;
}

function resultRow(line: LabReportResultLine, ctx: PrintContext): string {
  const info = flagInfo(line.flag);
  const flag = info && info.severity > 0 ? ctx.t(info.key) : '';
  const value = line.value ? `${line.value}${line.unit ? ` ${line.unit}` : ''}` : ctx.t('lab.pending');
  const micro = [
    line.organism ? `${ctx.t('lab.organism')}: ${line.organism}` : null,
    line.colonyCount ? `${ctx.t('lab.colonyCount')}: ${line.colonyCount}` : null,
    ...line.susceptibilities.map((s) => `${s.antibiotic}: ${ctx.t(SUSCEPTIBILITY_KEY[s.interpretation])}${s.mic ? ` (MIC ${s.mic})` : ''}`),
  ].filter(Boolean);
  return `<tr class="avoid-break"><td>${escapeHtml(line.name)}${line.remarks ? `<div class="small muted">${escapeHtml(line.remarks)}</div>` : ''}${
    micro.length ? `<div class="small">${micro.map((m) => escapeHtml(m)).join('<br/>')}</div>` : ''
  }</td><td class="flag-${info?.severity ?? 0}">${escapeHtml(value)}</td><td>${escapeHtml(line.referenceText ?? '')}</td><td class="flag-${
    info?.severity ?? 0
  }">${escapeHtml(flag)}</td></tr>`;
}

export function labReportDocument(report: LabReportView, ctx: PrintContext): PrintableDocument {
  const { t, f } = ctx;
  const title = t('reports.kind.Laboratory');
  const sections = report.sections
    .map(
      (section) =>
        `<h2>${escapeHtml(t(SECTION_KEY[section.section]))}</h2>${section.tests
          .map(
            (test) =>
              `<h3>${escapeHtml(test.name)}</h3><table><thead><tr><th>${escapeHtml(t('common.details'))}</th><th>${escapeHtml(t('lab.result'))}</th><th>${escapeHtml(
                t('lab.range'),
              )}</th><th></th></tr></thead><tbody>${test.results.map((line) => resultRow(line, ctx)).join('')}</tbody></table>${
                test.interpretation ? `<div class="box small"><strong>${escapeHtml(t('lab.note'))}:</strong> ${escapeMultiline(test.interpretation)}</div>` : ''
              }`,
          )
          .join('')}`,
    )
    .join('');
  const amendments = report.amendments.length
    ? `<h2>${escapeHtml(t('reports.changes'))}</h2><ul>${report.amendments
        .map((a) => `<li>${escapeHtml(t('reports.change', { revision: a.revision, date: f.dateTime(a.amendedAt), reason: a.reason }))}</li>`)
        .join('')}</ul>`
    : '';
  const body = `${letterhead(report.letterhead, title)}${patientLine(report.patient, ctx, [
    [t('reports.numberLabel'), report.reportNumber],
    [t('reports.releasedOn'), report.releasedAt ? f.dateTime(report.releasedAt) : null],
    [t('reports.referredBy'), report.referredBy],
  ])}${sections}${amendments}${signatories(report.signatories, ctx)}${footer(report.verificationCode, ctx)}`;
  return { title: `${title} ${report.reportNumber}`, body, styles: REPORT_STYLES, fileName: safeFileName(`lab-report-${report.reportNumber}`) };
}

export function radiologyReportDocument(report: RadiologyReportView, ctx: PrintContext): PrintableDocument {
  const { t, f } = ctx;
  const title = `${t(MODALITY_KEY[report.modality])}: ${report.studyName}`;
  const block = (label: string, text: string | null) => (text ? `<h2>${escapeHtml(label)}</h2><div>${escapeMultiline(text)}</div>` : '');
  const addenda = report.addenda.length
    ? `<h2>${escapeHtml(t('rad.addenda'))}</h2>${report.addenda
        .map((a) => `<div class="box"><div class="small muted">${escapeHtml(f.dateTime(a.addedAt))}</div>${escapeMultiline(a.text)}</div>`)
        .join('')}`
    : '';
  const body = `${letterhead(report.letterhead, title)}${patientLine(report.patient, ctx, [
    [t('reports.numberLabel'), report.accessionNumber],
    [t('rad.acquired'), report.acquiredAt ? f.dateTime(report.acquiredAt) : null],
    [t('reports.referredBy'), report.referredBy],
  ])}${block(t('rad.history'), report.clinicalHistory)}${block(t('rad.technique'), report.technique)}${block(t('rad.comparison'), report.comparison)}${block(
    t('rad.findings'),
    report.findings,
  )}<div class="box">${block(t('rad.impression'), report.impression)}</div>${addenda}${signatories(report.signatories, ctx)}${footer(report.verificationCode, ctx)}`;
  return { title, body, styles: REPORT_STYLES, fileName: safeFileName(`scan-report-${report.accessionNumber}`) };
}

export function cardiologyReportDocument(report: CardiologyReportView, ctx: PrintContext): PrintableDocument {
  const { t, f } = ctx;
  const title = `${t(CARDIOLOGY_TYPE_KEY[report.studyType])}: ${report.studyName}`;
  const measurements = cardiologyMeasurements(report, t);
  const table = measurements.length
    ? `<h2>${escapeHtml(t('card.measurements'))}</h2><table><tbody>${measurements
        .map((m) => `<tr><td>${escapeHtml(m.label)}</td><td><strong>${escapeHtml(m.value)}</strong></td></tr>`)
        .join('')}</tbody></table>`
    : '';
  const body = `${letterhead(report.letterhead, title)}${patientLine(report.patient, ctx, [
    [t('reports.numberLabel'), report.accessionNumber],
    [t('card.recorded'), report.acquiredAt ? f.dateTime(report.acquiredAt) : null],
    [t('reports.referredBy'), report.referredBy],
  ])}${table}${report.conclusion ? `<div class="box"><h2>${escapeHtml(t('card.conclusion'))}</h2>${escapeMultiline(report.conclusion)}</div>` : ''}${
    report.recommendations ? `<h2>${escapeHtml(t('card.advice'))}</h2><div>${escapeMultiline(report.recommendations)}</div>` : ''
  }${signatories(report.signatories, ctx)}${footer(report.verificationCode, ctx)}`;
  return { title, body, styles: REPORT_STYLES, fileName: safeFileName(`heart-test-${report.accessionNumber}`) };
}
