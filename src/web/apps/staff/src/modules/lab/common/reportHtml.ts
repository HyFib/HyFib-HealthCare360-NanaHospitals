/**
 * A4 report frame shared by lab, radiology and cardiology prints (UX-09): letterhead, patient block, body, signatories
 * with qualification and council registration, QR verification and the end-of-report line. Pure HTML builders (images
 * and the QR SVG are passed in), every value escaped.
 */
import { escapeHtml } from '@hc360/core';
import type { ReportLetterhead } from './types';

export interface PrintSignatory {
  name: string;
  qualification: string | null;
  registrationNumber: string | null;
  signatureDataUrl?: string | null;
}

export interface ReportShell {
  /** "Laboratory report", "Radiology report" … */
  title: string;
  letterhead: ReportLetterhead;
  logoDataUrl?: string | null;
  /** Label/value pairs in the patient block (two columns). */
  patientRows: readonly (readonly [string, string])[];
  bodyHtml: string;
  signatories: readonly PrintSignatory[];
  qr?: { svg: string; code: string; url: string } | null;
  /** Shown as a banner when the copy must not be mistaken for a final report, e.g. "Preliminary: not released". */
  statusBanner?: string | null;
  notes?: readonly string[];
}

/** Free text (findings, impressions, remarks) with paragraphs and line breaks kept. */
export function paragraphs(text: string | null | undefined): string {
  const value = (text ?? '').trim();
  if (!value) {
    return '';
  }
  return value
    .split(/\n{2,}/)
    .map((block) => `<p>${escapeHtml(block).replace(/\n/g, '<br/>')}</p>`)
    .join('');
}

function letterheadHtml(letterhead: ReportLetterhead, logo: string | null | undefined): string {
  const contact = [letterhead.phone ? `Phone ${letterhead.phone}` : null, letterhead.email].filter(Boolean).join(' · ');
  return `
  <header class="lh">
    ${logo ? `<img class="logo" src="${escapeHtml(logo)}" alt="" />` : ''}
    <div class="lh-text">
      <div class="facility">${escapeHtml(letterhead.facilityName)}</div>
      ${letterhead.address ? `<div class="muted">${escapeHtml(letterhead.address)}</div>` : ''}
      ${contact ? `<div class="muted">${escapeHtml(contact)}</div>` : ''}
      ${letterhead.nabhNumber ? `<div class="muted">NABH accreditation ${escapeHtml(letterhead.nabhNumber)}</div>` : ''}
      ${letterhead.letterheadNote ? `<div class="muted">${escapeHtml(letterhead.letterheadNote)}</div>` : ''}
    </div>
  </header>`;
}

function patientBlock(rows: ReportShell['patientRows']): string {
  const cells = rows.map(
    ([label, value]) =>
      `<div class="pb-cell"><span class="pb-label">${escapeHtml(label)}</span><span class="pb-value">${escapeHtml(value)}</span></div>`,
  );
  return `<section class="pb">${cells.join('')}</section>`;
}

function signatoriesHtml(signatories: ReportShell['signatories']): string {
  if (signatories.length === 0) {
    return '';
  }
  return `<section class="signs">${signatories
    .map(
      (s) => `
    <div class="sign">
      ${s.signatureDataUrl ? `<img class="sign-img" src="${escapeHtml(s.signatureDataUrl)}" alt="" />` : '<div class="sign-space"></div>'}
      <div class="sign-name">${escapeHtml(s.name)}</div>
      ${s.qualification ? `<div class="muted">${escapeHtml(s.qualification)}</div>` : ''}
      ${s.registrationNumber ? `<div class="muted">Reg. no. ${escapeHtml(s.registrationNumber)}</div>` : ''}
    </div>`,
    )
    .join('')}</section>`;
}

/** The complete report body (goes into printDocument with REPORT_STYLES). */
export function reportShellHtml(shell: ReportShell): string {
  const notes = (shell.notes ?? []).filter(Boolean);
  return `
  <div class="report">
    ${letterheadHtml(shell.letterhead, shell.logoDataUrl)}
    <h1 class="title">${escapeHtml(shell.title)}</h1>
    ${shell.statusBanner ? `<div class="banner">${escapeHtml(shell.statusBanner)}</div>` : ''}
    ${patientBlock(shell.patientRows)}
    <main class="body">${shell.bodyHtml}</main>
    ${notes.length ? `<section class="notes">${notes.map((n) => `<div>${escapeHtml(n)}</div>`).join('')}</section>` : ''}
    <footer class="foot">
      ${signatoriesHtml(shell.signatories)}
      ${
        shell.qr
          ? `<div class="qr">${shell.qr.svg}<div class="qr-text"><strong>Scan to verify this report</strong><div class="muted">Verification code ${escapeHtml(shell.qr.code)}</div><div class="muted url">${escapeHtml(shell.qr.url)}</div></div></div>`
          : ''
      }
      <div class="end">— End of report —</div>
    </footer>
  </div>`;
}

export const REPORT_STYLES = `
  .report { font-size: 10pt; }
  .lh { display: flex; gap: 12px; align-items: center; border-bottom: 2px solid #045E80; padding-bottom: 6px; }
  .logo { max-height: 18mm; max-width: 40mm; object-fit: contain; }
  .facility { font-size: 15pt; font-weight: 700; color: #045E80; }
  .title { text-align: center; font-size: 12pt; letter-spacing: 1px; text-transform: uppercase; margin: 8px 0 6px; }
  .banner { border: 1.5px solid #B10E1C; color: #B10E1C; text-align: center; font-weight: 700; padding: 3px; margin-bottom: 6px; }
  .pb { display: grid; grid-template-columns: 1fr 1fr; gap: 2px 16px; border: 1px solid #999; border-radius: 4px; padding: 6px 8px; margin-bottom: 8px; }
  .pb-cell { display: flex; gap: 6px; }
  .pb-label { color: #555; min-width: 30mm; }
  .pb-value { font-weight: 600; }
  .body p { margin: 0 0 6px; }
  .body h2 { font-size: 11pt; margin: 10px 0 4px; border-bottom: 1px solid #ccc; padding-bottom: 2px; }
  .body h3 { font-size: 10pt; margin: 8px 0 2px; }
  .body table { margin-bottom: 6px; }
  .body th { border-bottom: 1px solid #999; font-weight: 600; font-size: 9pt; color: #333; }
  .body td { border-bottom: 1px solid #eee; }
  .flag { font-weight: 700; }
  .abn { font-weight: 700; }
  .crit { font-weight: 800; color: #B10E1C; }
  .small { font-size: 8.5pt; color: #444; }
  .notes { margin-top: 8px; font-size: 8.5pt; color: #333; }
  .foot { margin-top: 14px; page-break-inside: avoid; }
  .signs { display: flex; gap: 24px; justify-content: flex-end; flex-wrap: wrap; }
  .sign { text-align: center; min-width: 45mm; }
  .sign-img { max-height: 14mm; max-width: 45mm; }
  .sign-space { height: 14mm; }
  .sign-name { font-weight: 700; border-top: 1px solid #666; padding-top: 2px; }
  .qr { display: flex; gap: 8px; align-items: center; margin-top: 10px; }
  .qr svg { width: 24mm; height: 24mm; }
  .qr-text { font-size: 8.5pt; }
  .url { word-break: break-all; }
  .end { text-align: center; margin-top: 8px; font-size: 8.5pt; color: #555; }
`;
