import { verificationPagePath } from '@/modules/verify/verification';
import type { ReportKindSegment } from './api';

/** Element that holds the report's QR code on screen; printing copies its SVG (see `safeSvgMarkup`). */
export const REPORT_QR_ID = 'hc360-report-qr';

/**
 * The address a report's QR code opens: the server's public check page when it knows the hospital's public address
 * (`portal.baseUrl`), otherwise the same page on the address the patient is using now. Null without a code.
 */
export function reportVerifyUrl(
  kind: ReportKindSegment,
  report: { verificationCode: string | null; verificationUrl?: string | null },
  origin: string,
): string | null {
  if (report.verificationUrl && /^https:\/\//i.test(report.verificationUrl)) {
    return report.verificationUrl;
  }
  return report.verificationCode ? `${origin}/portal${verificationPagePath(kind, report.verificationCode)}` : null;
}

/** The QR code's SVG as rendered on screen, for the printed copy (null when there is none). */
export function renderedReportQr(): string | null {
  return typeof document === 'undefined' ? null : (document.getElementById(REPORT_QR_ID)?.innerHTML ?? null);
}
