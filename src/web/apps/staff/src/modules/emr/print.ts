import { printDocument, printHtml } from '@hc360/core';
import { barcodeSvg } from '@hc360/ui';
import { verificationLink } from '@/lib/verification';
import type { DischargeSummaryPrint } from './api';
import type { PrescriptionPrintModel } from './prescriptionsApi';
import { CLINICAL_PRINT_STYLES, dischargeSummaryHtml, encounterSummaryHtml, prescriptionHtml, type EncounterPrintData } from './printTemplates';

/** A5 prescription with the QR verification code (the encoder loads on first use). */
export async function printPrescription(model: PrescriptionPrintModel, facilityName: string): Promise<void> {
  const verifyUrl = verificationLink(model.verificationUrl, model.verificationPath);
  const qrSvg = verifyUrl ? await barcodeSvg(verifyUrl, 'qrcode', { scale: 3 }) : null;
  const body = prescriptionHtml(model, { facilityName, qrSvg, verifyUrl });
  await printHtml(printDocument(body, { title: `Prescription ${model.prescriptionNumber}`, pageSize: 'A5', styles: CLINICAL_PRINT_STYLES }), { pageSize: 'A5' });
}

export async function printEncounterSummary(data: EncounterPrintData): Promise<void> {
  const body = encounterSummaryHtml(data);
  await printHtml(printDocument(body, { title: `Consultation summary ${data.patient.uhid}`, pageSize: 'A4', styles: CLINICAL_PRINT_STYLES }), { pageSize: 'A4' });
}

export async function printDischargeSummary(print: DischargeSummaryPrint, facilityName: string): Promise<void> {
  const body = dischargeSummaryHtml(print, facilityName);
  await printHtml(printDocument(body, { title: `Discharge summary ${print.patient.uhid}`, pageSize: 'A4', styles: CLINICAL_PRINT_STYLES }), { pageSize: 'A4' });
}
