import { printReport } from '@/modules/lab/common/reportPrint';
import type { RadiologyReportView } from './api';
import { RADIOLOGY_PRINT_STYLES, radiologyPatientRows, radiologyReportBodyHtml } from './reportBody';

/** A4 radiology report with letterhead, signatories and QR verification; unsigned copies carry a "not final" banner. */
export async function printRadiologyReport(view: RadiologyReportView): Promise<void> {
  const signed = view.status === 'Verified' || view.status === 'Released';
  await printReport({
    title: 'Radiology report',
    documentTitle: `Radiology report ${view.accessionNumber}`,
    letterhead: view.letterhead,
    patientRows: radiologyPatientRows(view),
    bodyHtml: radiologyReportBodyHtml(view),
    signatories: view.signatories,
    verificationCode: view.verificationCode,
    verificationPath: view.verificationPath,
    statusBanner: signed ? (view.status === 'Verified' ? 'Signed, not yet released' : null) : 'Draft: not signed by the radiologist',
    notes: ['This report is a professional opinion and should be correlated clinically.'],
    extraStyles: RADIOLOGY_PRINT_STYLES,
  });
}
