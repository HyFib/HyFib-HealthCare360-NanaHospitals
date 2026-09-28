import { escapeHtml, formatDateTime, printDocument, printHtml } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';

export interface UhidCardData {
  facilityName: string;
  uhid: string;
  fullName: string;
  ageSex: string;
  phone?: string | null;
  registeredAt?: string | null;
}

/** Patient ID card / sticker with a scannable UHID barcode (fits a 50×25 mm label or prints on A5). */
export async function printUhidCard(data: UhidCardData, pageSize: 'Label50x25' | 'A5' = 'A5'): Promise<void> {
  const barcode = await barcodeSvg(data.uhid, 'code128', { height: 9 });
  const body = `
    <div class="card">
      <div class="hospital">${escapeHtml(data.facilityName)}</div>
      <div class="name">${escapeHtml(data.fullName)}</div>
      <div class="meta">${escapeHtml(data.ageSex)}${data.phone ? ` · ${escapeHtml(data.phone)}` : ''}</div>
      <div class="barcode">${barcode}</div>
      <div class="uhid">UHID ${escapeHtml(data.uhid)}</div>
      ${data.registeredAt ? `<div class="meta">Registered ${escapeHtml(formatDateTime(data.registeredAt))}</div>` : ''}
    </div>`;
  const styles = `
    .card { border: 1px solid #888; border-radius: 6px; padding: 10px 12px; width: 86mm; font-family: 'Segoe UI', sans-serif; }
    .hospital { font-weight: 600; font-size: 11pt; color: #045E80; }
    .name { font-weight: 700; font-size: 13pt; margin-top: 4px; }
    .meta { font-size: 9pt; color: #333; }
    .barcode svg { width: 100%; height: 14mm; margin-top: 6px; }
    .uhid { font-family: Consolas, monospace; font-size: 11pt; letter-spacing: 1px; text-align: center; }`;
  await printHtml(printDocument(body, { title: `UHID ${data.uhid}`, pageSize, styles }), { pageSize });
}

export interface TokenSlipData {
  facilityName: string;
  tokenLabel: string;
  department: string;
  doctor?: string | null;
  patientName: string;
  uhid: string;
  visitNumber: string;
  issuedAt: string;
}

/** 80 mm thermal slip handed to the patient at check-in. */
export async function printTokenSlip(data: TokenSlipData): Promise<void> {
  const body = `
    <div class="slip">
      <div class="hospital">${escapeHtml(data.facilityName)}</div>
      <div class="label">Your token</div>
      <div class="token">${escapeHtml(data.tokenLabel)}</div>
      <div class="dept">${escapeHtml(data.department)}${data.doctor ? `<br/>${escapeHtml(data.doctor)}` : ''}</div>
      <hr/>
      <div>${escapeHtml(data.patientName)}</div>
      <div class="small">UHID ${escapeHtml(data.uhid)} · Visit ${escapeHtml(data.visitNumber)}</div>
      <div class="small">${escapeHtml(formatDateTime(data.issuedAt))}</div>
      <div class="note">Please wait in the waiting area. Your token number will be shown on the screen and called out.</div>
    </div>`;
  const styles = `
    .slip { width: 72mm; font-family: 'Segoe UI', sans-serif; text-align: center; font-size: 10pt; }
    .hospital { font-weight: 700; font-size: 11pt; }
    .label { margin-top: 6px; font-size: 9pt; text-transform: uppercase; letter-spacing: 1px; }
    .token { font-size: 34pt; font-weight: 800; line-height: 1.1; }
    .dept { font-weight: 600; }
    .small { font-size: 8pt; color: #333; }
    .note { margin-top: 6px; font-size: 8pt; }
    hr { border: none; border-top: 1px dashed #666; margin: 6px 0; }`;
  await printHtml(printDocument(body, { title: `Token ${data.tokenLabel}`, pageSize: 'Receipt80', styles }), { pageSize: 'Receipt80', silent: true });
}
