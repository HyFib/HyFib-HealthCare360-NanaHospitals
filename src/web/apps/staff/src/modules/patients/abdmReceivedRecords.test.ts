import { describe, expect, it } from 'vitest';
import { bundleSummary } from './abdmReceivedRecords';

// What a doctor reads of a FHIR bundle another provider sent: the clinical entries and the files, not the header.

describe('bundleSummary', () => {
  it('lists clinical entries with their values and the files inside, skipping who and for whom', () => {
    const { items, attachments } = bundleSummary({
      resourceType: 'Bundle',
      entry: [
        { resource: { resourceType: 'Composition', title: 'Discharge summary' } },
        { resource: { resourceType: 'Patient' } },
        { resource: { resourceType: 'Condition', code: { coding: [{ code: 'E11', display: 'Type 2 diabetes' }] } } },
        {
          resourceType: undefined,
        } as never,
        {
          resource: {
            resourceType: 'Observation',
            code: { text: 'HbA1c' },
            valueQuantity: { value: 7.2, unit: '%' },
          },
        },
        {
          resource: {
            resourceType: 'MedicationRequest',
            medicationCodeableConcept: { text: 'Metformin 500 mg' },
            dosageInstruction: [{ text: 'Twice a day after food' }],
          },
        },
        {
          resource: {
            resourceType: 'DocumentReference',
            type: { text: 'Lab report' },
            content: [{ attachment: { contentType: 'application/pdf', data: 'JVBERi0=', title: 'HbA1c report' } }],
          },
        },
      ],
    });
    expect(items).toEqual([
      { type: 'Diagnosis', label: 'Type 2 diabetes', detail: null },
      { type: 'Observation', label: 'HbA1c', detail: '7.2 %' },
      { type: 'Medicine', label: 'Metformin 500 mg', detail: 'Twice a day after food' },
      { type: 'Document', label: 'Lab report', detail: null },
    ]);
    expect(attachments).toEqual([{ title: 'HbA1c report', contentType: 'application/pdf', data: 'JVBERi0=' }]);
  });

  it('is empty for no bundle', () => {
    expect(bundleSummary(null)).toEqual({ items: [], attachments: [] });
  });
});
