import { printDocument, printHtml } from '@hc360/core';
import { CLINICAL_PRINT_STYLES } from '@/modules/emr/printTemplates';
import type { CheckupReport } from './api';
import { checkupReportHtml } from './reportTemplate';

/** A4 consolidated health check-up report. */
export async function printCheckupReport(report: CheckupReport, facilityName: string): Promise<void> {
  const body = checkupReportHtml(report, facilityName);
  await printHtml(
    printDocument(body, {
      title: `Health check-up ${report.booking.bookingNumber}`,
      pageSize: 'A4',
      styles: CLINICAL_PRINT_STYLES,
    }),
    { pageSize: 'A4' },
  );
}
