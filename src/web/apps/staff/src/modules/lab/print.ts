import { printDocument, printHtml } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import type { LabReportView, SampleLabel } from './api';
import { printReport } from './common/reportPrint';
import { LABEL_STYLES, sampleLabelHtml } from './labelHtml';
import { labReportBodyHtml, labReportNotes, labReportPatientRows } from './reportBody';

/**
 * Prints one 50 × 25 mm label per tube (Code 128). In HC360 Desktop the labels go straight to the label printer
 * without a dialog, so the phlebotomist can stick them on while the patient is still seated.
 */
export async function printSampleLabels(labels: readonly SampleLabel[]): Promise<void> {
  if (labels.length === 0) {
    return;
  }
  const barcodes = await Promise.all(labels.map((label) => barcodeSvg(label.barcode, 'code128', { height: 8, scale: 2 })));
  const body = labels.map((label, index) => sampleLabelHtml(label, barcodes[index] ?? '')).join('');
  const title = labels.length === 1 ? `Label ${labels[0]!.barcode}` : `${labels.length} sample labels`;
  await printHtml(printDocument(body, { title, pageSize: 'Label50x25', styles: LABEL_STYLES }), { pageSize: 'Label50x25', silent: true });
}

const LAB_REPORT_STYLES = `
  .abg { width: auto; min-width: 60%; margin: 4px 0; }
  .abg th, .abg td { padding: 1px 6px; font-size: 9pt; }
`;

/** A4 lab report with letterhead, signatories and QR verification. Unreleased copies carry a "not final" banner. */
export async function printLabReport(view: LabReportView): Promise<void> {
  const released = view.status === 'Released' || view.status === 'Amended';
  await printReport({
    title: 'Laboratory report',
    documentTitle: `Lab report ${view.reportNumber}`,
    letterhead: view.letterhead,
    patientRows: labReportPatientRows(view),
    bodyHtml: labReportBodyHtml(view, false),
    signatories: view.signatories,
    verificationCode: view.verificationCode,
    verificationPath: view.verificationPath,
    verificationUrl: view.verificationUrl,
    statusBanner: released ? null : 'Preliminary copy for the laboratory: not released',
    notes: labReportNotes(view),
    extraStyles: LAB_REPORT_STYLES,
  });
}
