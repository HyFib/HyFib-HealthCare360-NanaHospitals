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
  await printHtml(printDocument(body, { title: `Token ${data.tokenLabel}`, pageSize: 'Receipt80', styles }), {
    pageSize: 'Receipt80',
    silent: true,
  });
}

export interface DowntimeUhidSheetData {
  facilityName: string;
  label: string;
  issuedAt: string;
  numbers: { position: number; uhid: string }[];
}

/**
 * A downtime kit's UHID sheet (A4): each UHID of the block with its barcode, to be cut out for the patient's card or
 * wristband, and a line for the desk to write who was registered under it and when, so the forms can be matched when
 * HC360 is back.
 */
export async function printDowntimeUhidSheet(data: DowntimeUhidSheetData): Promise<void> {
  const rows = await Promise.all(
    data.numbers.map(async (n) => {
      const barcode = await barcodeSvg(n.uhid, 'code128', { height: 8 });
      return `
      <tr>
        <td class="pos">${n.position}</td>
        <td class="code"><div class="barcode">${barcode}</div><div class="uhid">${escapeHtml(n.uhid)}</div></td>
        <td></td>
        <td></td>
        <td></td>
      </tr>`;
    }),
  );
  const body = `
    <h1>${escapeHtml(data.facilityName)}: downtime UHIDs</h1>
    <div class="meta">${escapeHtml(data.label)} · issued ${escapeHtml(formatDateTime(data.issuedAt))} · ${data.numbers.length} UHIDs</div>
    <p class="note">Use these only while HC360 cannot be reached, in order. Write the UHID on the paper registration form
    and give the patient its barcode. Cross out a spoilt one and write "void". When HC360 is back, enter each form as a
    registration from paper with this UHID and the time on the form.</p>
    <table>
      <thead><tr><th>#</th><th>UHID</th><th>Patient's name</th><th>Date and time</th><th>Entered in HC360 (initials)</th></tr></thead>
      <tbody>${rows.join('')}</tbody>
    </table>`;
  const styles = `
    h1 { font-size: 14pt; margin: 0; }
    .meta { font-size: 9pt; color: #333; margin-top: 2px; }
    .note { font-size: 9pt; margin: 6px 0 8px; }
    table { width: 100%; border-collapse: collapse; font-size: 9pt; }
    th, td { border: 1px solid #888; padding: 3px 6px; vertical-align: middle; }
    th { background: #eee; text-align: left; }
    tr { page-break-inside: avoid; }
    td.pos { width: 6mm; text-align: right; }
    td.code { width: 52mm; }
    .barcode svg { width: 48mm; height: 9mm; }
    .uhid { font-family: Consolas, monospace; font-size: 10pt; letter-spacing: 1px; }`;
  await printHtml(printDocument(body, { title: `Downtime UHIDs ${data.label}`, pageSize: 'A4', styles }), {
    pageSize: 'A4',
  });
}
