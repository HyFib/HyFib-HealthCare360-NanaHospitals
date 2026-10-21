import { todayIso } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { documentFormDefaults, documentFormSchema, toAttachRequest, type DocumentFormValues } from './documentForm';

const report = new File(['%PDF-1.7'], 'mri-report.pdf', { type: 'application/pdf' });

const values = (patch: Partial<DocumentFormValues>): DocumentFormValues => ({
  ...documentFormDefaults(),
  file: report,
  ...patch,
});

/** The messages the schema gives, by field. */
function errors(v: DocumentFormValues): Record<string, string> {
  const result = documentFormSchema.safeParse(v);
  if (result.success) {
    return {};
  }
  return Object.fromEntries(result.error.issues.map((issue) => [issue.path.join('.'), issue.message]));
}

describe('attach document form', () => {
  it('starts as an outside report and needs the file', () => {
    expect(documentFormDefaults().type).toBe('ExternalReport');
    expect(errors(documentFormDefaults())).toEqual({ file: 'Choose the file to attach.' });
  });

  it('sends the type, the date on the document and where it came from', () => {
    const v = values({ title: ' MRI brain ', documentDate: '2026-08-14', source: ' Apollo Diagnostics, Pune ' });
    expect(errors(v)).toEqual({});
    expect(toAttachRequest(documentFormSchema.parse(v), 'f1')).toEqual({
      fileId: 'f1',
      type: 'ExternalReport',
      title: 'MRI brain',
      documentDate: '2026-08-14',
      source: 'Apollo Diagnostics, Pune',
      notes: null,
    });
  });

  it('names the document after its file, and sends no date or source when none is given', () => {
    expect(toAttachRequest(documentFormSchema.parse(values({ type: 'IdProof', title: '  ' })), 'f1')).toEqual({
      fileId: 'f1',
      type: 'IdProof',
      title: 'mri-report.pdf',
      documentDate: null,
      source: null,
      notes: null,
    });
  });

  it('takes a document dated today in the hospital, not one dated later', () => {
    expect(errors(values({ documentDate: todayIso() }))).toEqual({});
    expect(errors(values({ documentDate: todayIso(1) }))).toEqual({
      documentDate: 'The document date cannot be in the future.',
    });
  });

  it('keeps the title and source within what the server keeps', () => {
    expect(errors(values({ title: 'x'.repeat(201), source: 'y'.repeat(201) }))).toEqual({
      title: 'Use at most 200 characters.',
      source: 'Use at most 200 characters.',
    });
  });
});
