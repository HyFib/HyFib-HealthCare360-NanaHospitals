/**
 * 50 × 25 mm tube label (thermal label printer): patient name and UHID, Code 128 barcode with its digits, tube type and
 * bench, tests and collection time. Pure HTML builder: the barcode SVG is passed in so this stays testable.
 */
import { escapeHtml, HOSPITAL_TIME_ZONE } from '@hc360/core';
import type { SampleLabel } from './api';
import { containerInfo, sectionShort } from './labels';

const timeFormat = new Intl.DateTimeFormat('en-IN', {
  timeZone: HOSPITAL_TIME_ZONE,
  day: '2-digit',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

/** "26 Sep, 10:32" in hospital time (24-hour on labels: no am/pm to misread). */
export function labelTime(iso: string | null | undefined): string {
  return iso ? timeFormat.format(new Date(iso)) : '';
}

/**
 * Test names that fit on the label: as many as fit in `maxLength` characters, then "+n" for the rest, so a phlebotomist
 * can still see that more tests share the tube.
 */
export function labelTests(tests: readonly string[], maxLength = 30): string {
  const shown: string[] = [];
  let length = 0;
  for (const test of tests) {
    const extra = (shown.length ? 2 : 0) + test.length;
    if (shown.length > 0 && length + extra > maxLength) {
      break;
    }
    shown.push(test);
    length += extra;
  }
  const hidden = tests.length - shown.length;
  const text = shown.join(', ');
  const clipped = text.length > maxLength ? `${text.slice(0, maxLength - 1)}…` : text;
  return hidden > 0 ? `${clipped} +${hidden}` : clipped;
}

export const LABEL_STYLES = `
  .label { width: 46mm; height: 21mm; overflow: hidden; display: flex; flex-direction: column; justify-content: space-between;
           font-family: 'Segoe UI', Arial, sans-serif; font-size: 6.5pt; line-height: 1.15; color: #000; }
  .label + .label { page-break-before: always; break-before: page; }
  .row { display: flex; justify-content: space-between; gap: 1mm; white-space: nowrap; }
  .name { font-weight: 700; font-size: 7.5pt; overflow: hidden; text-overflow: ellipsis; max-width: 33mm; }
  .stat { font-weight: 800; border: 0.3mm solid #000; padding: 0 0.6mm; }
  .barcode svg { width: 100%; height: 7.5mm; display: block; }
  .digits { font-family: Consolas, 'Courier New', monospace; font-size: 7pt; font-weight: 600; letter-spacing: 0.3mm; }
  .tests { overflow: hidden; text-overflow: ellipsis; max-width: 30mm; }
`;

/** One label; `barcode` is the Code 128 SVG of `label.barcode`. */
export function sampleLabelHtml(label: SampleLabel, barcode: string): string {
  const tube = containerInfo[label.container];
  return `
  <div class="label">
    <div class="row"><span class="name">${escapeHtml(label.patientName)}</span><span>${escapeHtml(label.ageSex)}</span></div>
    <div class="row"><span>UHID ${escapeHtml(label.uhid)}</span>${label.priority === 'Stat' ? '<span class="stat">STAT</span>' : `<span>${escapeHtml(label.accessionNumber)}</span>`}</div>
    <div class="barcode">${barcode}</div>
    <div class="row"><span class="digits">${escapeHtml(label.barcode)}</span><span>${escapeHtml(tube.short)} · ${escapeHtml(sectionShort[label.section])}</span></div>
    <div class="row"><span class="tests">${escapeHtml(labelTests(label.tests))}</span><span>${escapeHtml(labelTime(label.collectedAt))}</span></div>
  </div>`;
}
