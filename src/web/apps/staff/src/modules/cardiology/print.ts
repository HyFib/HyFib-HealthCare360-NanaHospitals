import { printReport } from '@/modules/lab/common/reportPrint';
import type { CardiologyReportView } from './api';
import { studyTypeLabel } from './labels';
import { CARDIOLOGY_PRINT_STYLES, cardiologyPatientRows, cardiologyReportBodyHtml } from './reportBody';

/** A4 cardiology report (only the measurements of the study type) with signatories and QR verification. */
export async function printCardiologyReport(view: CardiologyReportView): Promise<void> {
  const signed = view.status === 'Verified' || view.status === 'Released';
  await printReport({
    title: `${studyTypeLabel[view.studyType]} report`,
    documentTitle: `Cardiology report ${view.accessionNumber}`,
    letterhead: view.letterhead,
    patientRows: cardiologyPatientRows(view),
    bodyHtml: cardiologyReportBodyHtml(view),
    signatories: view.signatories,
    verificationCode: view.verificationCode,
    verificationPath: view.verificationPath,
    statusBanner: signed ? (view.status === 'Verified' ? 'Signed, not yet released' : null) : 'Draft: not signed by the cardiologist',
    notes: view.files.length ? [`${view.files.length} device ${view.files.length === 1 ? 'recording is' : 'recordings are'} stored with this report.`] : [],
    extraStyles: CARDIOLOGY_PRINT_STYLES,
  });
}
