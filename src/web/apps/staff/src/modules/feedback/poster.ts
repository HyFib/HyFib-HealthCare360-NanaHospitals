import { escapeHtml } from '@hc360/core';

export interface PosterData {
  facilityName: string;
  formTitle: string;
  introText: string | null;
  /** The public form address the QR code opens. */
  url: string;
  /** Kiosk card: for a tablet stand instead of a wall poster. */
  kiosk?: boolean;
}

export const POSTER_STYLES = `
.poster { display: flex; flex-direction: column; align-items: center; text-align: center; gap: 6mm; padding-top: 8mm; }
.hospital { font-size: 16px; font-weight: 600; letter-spacing: 0.02em; color: #0f6cbd; }
.headline { font-size: 34px; font-weight: 700; line-height: 1.15; }
.sub { font-size: 16px; color: #333; max-width: 150mm; }
.qr { border: 2px solid #111; border-radius: 6mm; padding: 6mm; }
.qr svg { width: 90mm; height: 90mm; display: block; }
.steps { font-size: 15px; display: flex; gap: 10mm; justify-content: center; }
.steps b { display: block; font-size: 22px; color: #0f6cbd; }
.url { font-family: Consolas, monospace; font-size: 11px; color: #555; word-break: break-all; max-width: 160mm; }
.note { font-size: 12px; color: #555; }
.form { font-size: 12px; color: #777; }
`;

/** A4 poster (or kiosk card) with the form's QR code. Every text is escaped; the QR SVG comes from the local encoder. */
export function posterHtml(data: PosterData, qrSvg: string): string {
  return `<div class="poster">
  <div class="hospital">${escapeHtml(data.facilityName)}</div>
  <div class="headline">${data.kiosk ? 'Tell us about your visit' : 'How was your visit?'}</div>
  <div class="sub">${escapeHtml(data.introText ?? 'Your feedback helps us care for you better. It takes less than a minute.')}</div>
  <div class="qr">${qrSvg}</div>
  <div class="steps">
    <div><b>1</b>Open your phone camera</div>
    <div><b>2</b>Point it at the code</div>
    <div><b>3</b>Tap the link and answer</div>
  </div>
  <div class="note">Your answers are anonymous. For an urgent concern, please speak to the front desk.</div>
  <div class="url">${escapeHtml(data.url)}</div>
  <div class="form">${escapeHtml(data.formTitle)}</div>
</div>`;
}
