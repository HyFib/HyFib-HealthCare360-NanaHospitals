import { escapeHtml } from '@hc360/core';
import type { Language, Translate } from '@/i18n';
import type { Formatters } from '@/i18n/useFormat';
import { GENDER_KEY } from '@/lib/people';
import { escapeMultiline, safeFileName, safeSvgMarkup, type PrintableDocument } from '@/lib/print';
import type { PortalPrescriptionDetail, PortalProfile } from '@/lib/types';
import { frequencyText, medicineName } from './labels';

export interface PrescriptionPrintContext {
  t: Translate;
  f: Formatters;
  language: Language;
  printedAt: string;
  patient: Pick<PortalProfile, 'fullName' | 'uhid' | 'age' | 'gender'>;
  /** SVG markup of the verification QR code, rendered by the page (qrcode.react); null when not verifiable. */
  qrSvg: string | null;
  /** The address the QR code opens. */
  verifyUrl: string | null;
}

const STYLES = `
.head { display: flex; justify-content: space-between; gap: 12px; border-bottom: 2px solid #0A7CA7; padding-bottom: 6px; margin-bottom: 8px; }
.facility { font-size: 14pt; font-weight: 700; color: #045E80; }
.patient { display: flex; flex-wrap: wrap; gap: 4px 18px; margin: 6px 0 10px; }
.rx { font-size: 20pt; font-weight: 700; color: #045E80; margin: 4px 0; }
.qr { display: flex; gap: 10px; align-items: center; margin-top: 14px; }
.qr svg { width: 28mm; height: 28mm; }
.cancelled { border: 2px solid #c4314b; color: #c4314b; font-weight: 700; padding: 6px; margin: 8px 0; }
.footer { margin-top: 16px; font-size: 8.5pt; color: #555; border-top: 1px solid #ccc; padding-top: 4px; }
`;

/** The printable prescription: doctor, patient, medicines with directions in the patient's language, and the QR check. */
export function prescriptionDocument(rx: PortalPrescriptionDetail, ctx: PrescriptionPrintContext): PrintableDocument {
  const { t, f, language, patient } = ctx;
  const qr = safeSvgMarkup(ctx.qrSvg);
  const doctor = [rx.doctor, rx.doctorQualification].filter(Boolean).join(', ');
  const rows = rx.lines
    .map(
      (line, index) =>
        `<tr class="avoid-break"><td>${index + 1}</td><td><strong>${escapeHtml(medicineName(line))}</strong>${
          line.genericName ? `<div class="small muted">${escapeHtml(line.genericName)}</div>` : ''
        }</td><td>${escapeHtml(line.dose)}</td><td>${escapeHtml(frequencyText(line, language))}</td><td>${escapeHtml(
          t('rx.forDays', { count: line.durationDays }),
        )}</td><td class="right">${escapeHtml(line.quantity)}</td></tr>${
          line.instructions
            ? `<tr><td></td><td colspan="5" class="small">${escapeHtml(line.instructions)}</td></tr>`
            : ''
        }`,
    )
    .join('');
  const body = `<div class="head"><div><div class="facility">${escapeHtml(rx.facility ?? '')}</div><div><strong>${escapeHtml(doctor)}</strong></div>${
    rx.doctorRegistrationNumber
      ? `<div class="small muted">${escapeHtml(t('rx.registration'))} ${escapeHtml(rx.doctorRegistrationNumber)}</div>`
      : ''
  }</div><div class="right"><h1>${escapeHtml(t('rx.print.title'))}</h1><div class="small">${escapeHtml(t('rx.number', { number: rx.prescriptionNumber }))}</div><div class="small">${escapeHtml(
    f.dateTime(rx.signedAt),
  )}</div></div></div>
<div class="patient"><span><span class="muted">${escapeHtml(t('print.patient'))}:</span> <strong>${escapeHtml(patient.fullName)}</strong></span><span><span class="muted">${escapeHtml(
    t('common.uhid'),
  )}:</span> ${escapeHtml(patient.uhid)}</span>${
    patient.age || patient.gender !== 'Unknown'
      ? `<span>${escapeHtml([patient.age, patient.gender === 'Unknown' ? null : t(GENDER_KEY[patient.gender])].filter(Boolean).join(' · '))}</span>`
      : ''
  }</div>
${rx.status === 'Cancelled' ? `<div class="cancelled">${escapeHtml(t('rx.cancelledNote'))}</div>` : ''}
<div class="rx">&#8478;</div>
<table><thead><tr><th>#</th><th>${escapeHtml(t('rx.medicines'))}</th><th>${escapeHtml(t('rx.dose'))}</th><th>${escapeHtml(t('rx.howOften'))}</th><th></th><th class="right">${escapeHtml(t('rx.quantityLabel'))}</th></tr></thead><tbody>${rows}</tbody></table>
${rx.advice ? `<h2>${escapeHtml(t('rx.advice'))}</h2><div>${escapeMultiline(rx.advice)}</div>` : ''}
${
  qr && rx.status === 'Signed'
    ? `<div class="qr">${qr}<div class="small">${escapeHtml(t('rx.print.verify'))}${ctx.verifyUrl ? `<br/>${escapeHtml(ctx.verifyUrl)}` : ''}</div></div>`
    : ''
}
<div class="footer">${escapeHtml(t('rx.safety'))}<br/>${escapeHtml(t('print.printedOn', { date: f.dateTime(ctx.printedAt) }))}</div>`;
  return {
    title: `${t('rx.print.title')} ${rx.prescriptionNumber}`,
    body,
    styles: STYLES,
    fileName: safeFileName(`prescription-${rx.prescriptionNumber}`),
  };
}
