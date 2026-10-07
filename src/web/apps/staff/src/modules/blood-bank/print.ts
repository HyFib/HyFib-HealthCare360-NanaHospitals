import {
  escapeHtml,
  formatDate,
  formatDateTime,
  letterheadHtml,
  printDocument,
  printHtml,
  templateFor,
  type BloodUnitLabel,
  type Letterhead,
} from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { printLabelJob } from '@/lib/labelPrinting';
import type { BloodComponentType, BloodIssueDto, BloodRequestDetail, BloodUnitDto, CrossMatchDto } from './api';
import { componentShort, crossmatchMethodLabel, groupLabel, storageCondition } from './labels';
import { ttiState } from './rules';

/** Whether the unit may be issued: screening complete and non-reactive, and out of quarantine. */
function isCleared(unit: BloodUnitDto): boolean {
  return ttiState(unit.tti).cleared && unit.status !== 'Quarantine';
}

/** A unit label as the server writes it for a ZPL or TSPL label printer: the same facts as the label page. */
export function unitLabelData(unit: BloodUnitDto): BloodUnitLabel {
  return {
    unitNumber: unit.unitNumber,
    bloodGroup: unit.bloodGroup === 'Unknown' ? 'UNGROUPED' : groupLabel(unit.bloodGroup),
    component: componentShort[unit.componentType],
    volumeMl: unit.volumeMl,
    cleared: isCleared(unit),
    expiresAt: unit.expiresAt,
    storage: storageCondition[unit.componentType],
  };
}

/**
 * Blood unit labels on 50 × 25 mm label stock: group in large type, component and volume, a Code 128 barcode of the
 * unit number for scanning at issue and at the bedside, expiry, and the screening status. A unit still in
 * quarantine is labelled NOT FOR ISSUE. Printed without a dialog on this computer's label printer.
 */
export async function printUnitLabels(units: readonly BloodUnitDto[]): Promise<void> {
  if (units.length === 0) {
    return;
  }
  await printLabelJob({ kind: 'blood-units', labels: units.map(unitLabelData), page: () => unitLabelsPage(units) });
}

/** The unit labels as a page, for label printers on their Windows driver and for browsers. */
async function unitLabelsPage(units: readonly BloodUnitDto[]): Promise<string> {
  const labels = await Promise.all(
    units.map(async (unit) => {
      const barcode = await barcodeSvg(unit.unitNumber, 'code128', { height: 7, scale: 2 });
      const cleared = isCleared(unit);
      const group = unit.bloodGroup === 'Unknown' ? 'UNGROUPED' : groupLabel(unit.bloodGroup);
      return `
      <div class="label">
        <div class="top">
          <span class="group">${escapeHtml(group)}</span>
          <span class="comp">${escapeHtml(componentShort[unit.componentType])}<br/>${escapeHtml(unit.volumeMl)} ml</span>
          <span class="${cleared ? 'tti' : 'tti hold'}">${cleared ? 'TTI: NON-REACTIVE' : 'QUARANTINE<br/>NOT FOR ISSUE'}</span>
        </div>
        <div class="barcode">${barcode}</div>
        <div class="bottom">
          <span class="number">${escapeHtml(unit.unitNumber)}</span>
          <span>Exp ${escapeHtml(formatDate(unit.expiresAt))}</span>
        </div>
        <div class="store">${escapeHtml(storageCondition[unit.componentType])}</div>
      </div>`;
    }),
  );
  const styles = `
    .label { width: 46mm; height: 21mm; overflow: hidden; page-break-after: always; font-family: 'Segoe UI', Arial, sans-serif; }
    .label:last-child { page-break-after: auto; }
    .top { display: flex; align-items: center; justify-content: space-between; gap: 1mm; }
    .group { font-size: 15pt; font-weight: 800; line-height: 1; }
    .comp { font-size: 6.5pt; font-weight: 700; line-height: 1.1; text-align: center; }
    .tti { font-size: 5.5pt; font-weight: 700; text-align: right; line-height: 1.1; }
    .tti.hold { border: 1px solid #000; padding: 0 1mm; }
    .barcode svg { width: 100%; height: 7mm; }
    .bottom { display: flex; justify-content: space-between; font-size: 6.5pt; line-height: 1.1; }
    .number { font-family: Consolas, monospace; font-weight: 700; }
    .store { font-size: 5pt; }`;
  return printDocument(labels.join(''), {
    title: `Blood unit labels (${units.length})`,
    pageSize: 'Label50x25',
    styles,
  });
}

export interface IssueSlipUnit {
  expiresAt: string;
  volumeMl: number;
  componentType: BloodComponentType;
}

/**
 * Issue slip (80 mm) that travels with the unit to the ward: patient, unit barcode, cross-match or emergency release,
 * under the letterhead ("Other documents" template).
 */
export async function printIssueSlip(
  letterhead: Letterhead,
  request: BloodRequestDetail,
  issue: BloodIssueDto,
  crossMatch: CrossMatchDto | null,
  unit: IssueSlipUnit | null,
): Promise<void> {
  const barcode = await barcodeSvg(issue.unitNumber, 'code128', { height: 9 });
  const template = templateFor(letterhead, 'other');
  const compatibility = issue.isEmergencyRelease
    ? `<div class="alert">EMERGENCY RELEASE — NOT CROSS-MATCHED<br/>Authorised by ${escapeHtml(request.emergencyReleaseAuthorisedBy ?? '—')}</div>`
    : crossMatch
      ? `<div>Cross-match: <b>${escapeHtml(crossMatch.result.toUpperCase())}</b> (${escapeHtml(crossmatchMethodLabel[crossMatch.method])})<br/>${escapeHtml(
          formatDateTime(crossMatch.performedAt),
        )}${crossMatch.performedByName ? ` · ${escapeHtml(crossMatch.performedByName)}` : ''}</div>`
      : '<div>Cross-match: plasma component, ABO compatible</div>';
  const body = `
    <div class="slip">
      ${letterheadHtml(letterhead, template, { compact: true })}
      <div class="title">BLOOD ISSUE SLIP</div>
      <div class="small">Request ${escapeHtml(request.requestNumber)} · ${escapeHtml(formatDateTime(issue.issuedAt))}</div>
      <hr/>
      <div class="name">${escapeHtml(request.patientName)}</div>
      <div>UHID ${escapeHtml(request.uhid)}</div>
      <div>Patient group (blood bank): <b>${escapeHtml(groupLabel(request.patientBloodGroup))}</b></div>
      <hr/>
      <div class="barcode">${barcode}</div>
      <div class="unit">${escapeHtml(issue.unitNumber)}</div>
      <div><b>${escapeHtml(componentShort[issue.componentType])}</b> · group <b>${escapeHtml(groupLabel(issue.unitBloodGroup))}</b>${
        unit ? ` · ${escapeHtml(unit.volumeMl)} ml · exp ${escapeHtml(formatDate(unit.expiresAt))}` : ''
      }</div>
      ${compatibility}
      <hr/>
      <div>Issued by ${escapeHtml(issue.issuedByName ?? '—')}</div>
      <div>Collected by ${escapeHtml(issue.issuedToName)}</div>
      <div class="note">At the bedside, two staff check the patient's identity, the unit number, group and expiry against this slip. Start within 30 minutes; return an unused unit within 30 minutes with the cold chain kept. Stop the transfusion and report any reaction at once.</div>
      <div class="sign">Transfusion started at ______ by ______________</div>
    </div>`;
  const styles = `
    .slip { width: 72mm; font-family: 'Segoe UI', Arial, sans-serif; font-size: 9pt; }
    .title { font-weight: 800; font-size: 12pt; text-align: center; letter-spacing: 1px; }
    .small { font-size: 8pt; text-align: center; }
    .name { font-weight: 700; font-size: 11pt; }
    .barcode svg { width: 100%; height: 12mm; }
    .unit { font-family: Consolas, monospace; font-size: 11pt; font-weight: 700; text-align: center; }
    .alert { border: 2px solid #000; padding: 2px 4px; font-weight: 800; margin-top: 4px; }
    .note { font-size: 7.5pt; margin-top: 6px; }
    .sign { margin-top: 10px; font-size: 8pt; }
    hr { border: none; border-top: 1px dashed #666; margin: 5px 0; }`;
  await printHtml(
    printDocument(body, { title: `Blood issue ${issue.unitNumber}`, pageSize: 'Receipt80', styles, template }),
    { pageSize: 'Receipt80' },
  );
}
