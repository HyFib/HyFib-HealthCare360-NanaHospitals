/**
 * The attach-document form on the patient profile (EHR-04): its limits and rules mirror AttachDocumentRequestValidator
 * on the server, so staff see them before saving. Pure (no React, no Fluent) so it is unit-tested.
 */
import { todayIso } from '@hc360/core';
import { z } from 'zod';
import { PATIENT_DOCUMENT_TYPES, type AttachDocumentRequest } from './api';

/** The server's limits for a document's details. */
export const DOCUMENT_LIMITS = {
  title: 200,
  source: 200,
} as const;

const text = (max: number) => z.string().trim().max(max, `Use at most ${max} characters.`);

export const documentFormSchema = z
  .object({
    file: z.instanceof(File).nullable(),
    type: z.enum(PATIENT_DOCUMENT_TYPES),
    /** Left empty, the file's name stands for it. */
    title: text(DOCUMENT_LIMITS.title),
    /** The date on the document, "yyyy-MM-dd". */
    documentDate: z.string().nullable(),
    /** Where the document came from, such as an outside lab or hospital. */
    source: text(DOCUMENT_LIMITS.source),
  })
  .superRefine((v, ctx) => {
    if (!v.file) {
      ctx.addIssue({ code: 'custom', path: ['file'], message: 'Choose the file to attach.' });
    }
    // Today in the hospital (IST), as the server checks it.
    if (v.documentDate && v.documentDate > todayIso()) {
      ctx.addIssue({ code: 'custom', path: ['documentDate'], message: 'The document date cannot be in the future.' });
    }
  });

export type DocumentFormValues = z.infer<typeof documentFormSchema>;

export function documentFormDefaults(): DocumentFormValues {
  return { file: null, type: 'ExternalReport', title: '', documentDate: null, source: '' };
}

const clean = (value: string): string | null => value.trim() || null;

/** The request the server expects for the uploaded file; call it only with values the schema accepted. */
export function toAttachRequest(v: DocumentFormValues, fileId: string): AttachDocumentRequest {
  if (!v.file) {
    throw new Error('The file is required.');
  }
  return {
    fileId,
    type: v.type,
    // Without a title the file's name stands for it, cut to what the server keeps.
    title: clean(v.title) ?? v.file.name.slice(0, DOCUMENT_LIMITS.title),
    documentDate: v.documentDate || null,
    source: clean(v.source),
    notes: null,
  };
}
