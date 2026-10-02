/**
 * The record-consent form: its limits and rules mirror CaptureConsentRequestValidator on the server, so staff see them
 * before saving. Pure (no React, no Fluent) so it is unit-tested.
 */
import { isApiError } from '@hc360/core';
import { z } from 'zod';
import { CONSENT_TYPES, type CaptureConsentRequest } from './api';

/** The server's limits for a consent and for the reason it is withdrawn. */
export const CONSENT_LIMITS = {
  version: 16,
  witnessName: 200,
  relation: 64,
  signerName: 200,
  notes: 1000,
  reason: 300,
} as const;

const text = (max: number) => z.string().trim().max(max, `Use at most ${max} characters.`);

export const consentFormSchema = z
  .object({
    type: z.enum(CONSENT_TYPES).nullable(),
    decision: z.enum(['given', 'refused']),
    /** The patient signed, or someone signed on their behalf (a relative or guardian). */
    signer: z.enum(['patient', 'other']),
    signedByName: text(CONSENT_LIMITS.signerName),
    signedByRelation: text(CONSENT_LIMITS.relation),
    witnessName: text(CONSENT_LIMITS.witnessName),
    consentVersion: text(CONSENT_LIMITS.version),
    notes: text(CONSENT_LIMITS.notes),
  })
  .superRefine((v, ctx) => {
    if (!v.type) {
      ctx.addIssue({ code: 'custom', path: ['type'], message: 'Choose the consent.' });
    }
    if (v.signer === 'other') {
      if (!v.signedByName) {
        ctx.addIssue({
          code: 'custom',
          path: ['signedByName'],
          message: 'Enter the name of the person consenting on the patient’s behalf.',
        });
      }
      if (!v.signedByRelation) {
        ctx.addIssue({
          code: 'custom',
          path: ['signedByRelation'],
          message: 'Enter how they are related to the patient.',
        });
      }
    }
  });

export type ConsentFormValues = z.infer<typeof consentFormSchema>;

export function consentFormDefaults(): ConsentFormValues {
  return {
    type: null,
    decision: 'given',
    signer: 'patient',
    signedByName: '',
    signedByRelation: '',
    witnessName: '',
    consentVersion: '',
    notes: '',
  };
}

const clean = (value: string): string | null => value.trim() || null;

/** The request the server expects; call it only with values the schema accepted. */
export function toCaptureRequest(v: ConsentFormValues): CaptureConsentRequest {
  if (!v.type) {
    throw new Error('The consent type is required.');
  }
  const proxy = v.signer === 'other';
  return {
    type: v.type,
    consentVersion: clean(v.consentVersion),
    granted: v.decision === 'given',
    witnessName: clean(v.witnessName),
    signedByRelation: proxy ? clean(v.signedByRelation) : null,
    signedByName: proxy ? clean(v.signedByName) : null,
    documentFileId: null,
    notes: clean(v.notes),
  };
}

const FORM_FIELDS = ['type', 'signedByName', 'signedByRelation', 'witnessName', 'consentVersion', 'notes'] as const;
export type ConsentFormField = (typeof FORM_FIELDS)[number];

export interface ConsentServerErrors {
  /** Messages for fields the form shows, to be put on those fields. */
  fields: [ConsentFormField, string][];
  /** Everything else, to be shown above the buttons. */
  other: string[];
}

/**
 * Where a failed save's messages belong: validation messages (400, camelCase keys) go on their fields, the rest
 * (a field the form does not show, a restricted record, a lost connection…) above the buttons.
 */
export function consentServerErrors(error: unknown): ConsentServerErrors {
  if (!isApiError(error)) {
    return { fields: [], other: ['Please try again.'] };
  }
  if (!error.isValidation) {
    return { fields: [], other: [error.userMessage] };
  }
  const result: ConsentServerErrors = { fields: [], other: [] };
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const message = messages[0] ?? error.title;
    if ((FORM_FIELDS as readonly string[]).includes(key)) {
      result.fields.push([key as ConsentFormField, message]);
    } else {
      result.other.push(message);
    }
  }
  return result;
}
