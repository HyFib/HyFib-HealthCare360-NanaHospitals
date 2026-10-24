import type { DiagnosticReportStatus } from './types';

/**
 * Whether the server exports a diagnostics report as an ABDM health record (`AbdmRecordLoader` on the server): a lab
 * report once released (an amended one too), a radiology or cardiology report only once released. A draft, a report
 * waiting for sign-off and one signed off but not released are still the department's work, and the server refuses them.
 */
export function labReportExportable(status: string): boolean {
  return status === 'Released' || status === 'Amended';
}

export function imagingReportExportable(status: DiagnosticReportStatus | null | undefined): boolean {
  return status === 'Released';
}
