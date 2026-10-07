import { printDocument, printHtml, templateFor } from '@hc360/core';
import { CLINICAL_PRINT_STYLES, encounterSummaryHtml, type EncounterPrintData } from './printTemplates';

/** A4 consultation summary. Prescriptions and discharge summaries are printed from the server (`@/lib/documents`). */
export async function printEncounterSummary(data: EncounterPrintData): Promise<void> {
  const body = encounterSummaryHtml(data);
  await printHtml(
    printDocument(body, {
      title: `Consultation summary ${data.patient.uhid}`,
      pageSize: 'A4',
      styles: CLINICAL_PRINT_STYLES,
      template: templateFor(data.letterhead, 'clinical-note'),
    }),
    { pageSize: 'A4' },
  );
}
