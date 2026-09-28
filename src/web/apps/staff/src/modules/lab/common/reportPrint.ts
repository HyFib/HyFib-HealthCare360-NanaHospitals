import { printDocument, printHtml } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { fileAsDataUrl } from './files';
import { REPORT_STYLES, reportShellHtml } from './reportHtml';
import type { ReportLetterhead, ReportSignatory } from './types';

/**
 * Absolute link printed in the report QR code. The server returns the public verification path; anyone who scans it
 * sees only that the report is genuine (report number, initials, tests), never the results.
 */
export function verificationUrl(path: string | null | undefined): string | null {
  if (!path) {
    return null;
  }
  try {
    return new URL(path, window.location.origin).toString();
  } catch {
    return null;
  }
}

export interface PrintReportOptions {
  title: string;
  documentTitle: string;
  letterhead: ReportLetterhead;
  patientRows: readonly (readonly [string, string])[];
  bodyHtml: string;
  signatories: readonly ReportSignatory[];
  verificationCode: string | null;
  verificationPath: string | null;
  statusBanner?: string | null;
  notes?: readonly string[];
  extraStyles?: string;
}

/**
 * Prints an A4 diagnostic report with the facility logo, signatures and QR verification. Images are best effort: a
 * missing logo or signature never blocks printing.
 */
export async function printReport(options: PrintReportOptions): Promise<void> {
  const url = options.verificationCode ? verificationUrl(options.verificationPath) : null;
  const [logo, signatures, qrSvg] = await Promise.all([
    fileAsDataUrl(options.letterhead.logoFileId),
    Promise.all(options.signatories.map((signatory) => fileAsDataUrl(signatory.signatureFileId))),
    url ? barcodeSvg(url, 'qrcode', { scale: 3 }) : Promise.resolve(null),
  ]);

  const body = reportShellHtml({
    title: options.title,
    letterhead: options.letterhead,
    logoDataUrl: logo,
    patientRows: options.patientRows,
    bodyHtml: options.bodyHtml,
    signatories: options.signatories.map((signatory, index) => ({
      name: signatory.name,
      qualification: signatory.qualification,
      registrationNumber: signatory.registrationNumber,
      signatureDataUrl: signatures[index] ?? null,
    })),
    qr: url && qrSvg && options.verificationCode ? { svg: qrSvg, code: options.verificationCode, url } : null,
    statusBanner: options.statusBanner,
    notes: options.notes,
  });

  await printHtml(
    printDocument(body, { title: options.documentTitle, pageSize: 'A4', styles: REPORT_STYLES + (options.extraStyles ?? '') }),
    { pageSize: 'A4' },
  );
}
