import { verificationPageUrl } from '@/modules/verify/verification';
import type { ReportKindSegment } from './api';

/** Element that holds the report's QR code on screen; printing copies its SVG (see `safeSvgMarkup`). */
export const REPORT_QR_ID = 'hc360-report-qr';

/** The address a report's QR code opens (see `verificationPageUrl`). Null without a code. */
export function reportVerifyUrl(
  kind: ReportKindSegment,
  report: { verificationCode: string | null; verificationUrl?: string | null },
  origin: string,
): string | null {
  return verificationPageUrl(kind, report.verificationCode, report.verificationUrl, origin);
}

/** The QR code's SVG as rendered on screen, for the printed copy (null when there is none). */
export function renderedReportQr(): string | null {
  return typeof document === 'undefined' ? null : (document.getElementById(REPORT_QR_ID)?.innerHTML ?? null);
}
