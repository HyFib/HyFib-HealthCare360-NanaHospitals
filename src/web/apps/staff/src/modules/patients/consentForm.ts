/**
 * The record-consent form: its limits and rules mirror CaptureConsentRequestValidator on the server, so staff see them
 * before saving. Pure (no React, no Fluent) so it is unit-tested.
 */
import { isApiError, todayIso } from '@hc360/core';
import { z } from 'zod';
import { CONSENT_TYPES, SHARED_RECORD_KINDS, SHARING_PURPOSES, type CaptureConsentRequest } from './api';

/** The server's limits for a consent and for the reason it is withdrawn. */
export const CONSENT_LIMITS = {
  version: 16,
  witnessName: 200,
  relation: 64,
  signerName: 200,
  notes: 1000,
  reason: 300,
  /** The most partner accounts one record-sharing consent names. */
  partners: 20,
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
    /** Record sharing only: the whole record for every partner account, or only what the patient chose. */
    sharing: z.enum(['everything', 'limited']),
    shareWith: z.array(z.string()),
    purposes: z.array(z.enum(SHARING_PURPOSES)),
    kinds: z.array(z.enum(SHARED_RECORD_KINDS)),
    /** yyyy-MM-dd or null, as the date fields give them. */
    recordsFrom: z.string().nullable(),
    recordsTo: z.string().nullable(),
    until: z.string().nullable(),
  })
  .superRefine((v, ctx) => {
    if (!v.type) {
      ctx.addIssue({ code: 'custom', path: ['type'], message: 'Choose the consent.' });
    }
    if (limitsSharing(v)) {
      const chosen =
        v.shareWith.length + v.purposes.length + v.kinds.length > 0 || Boolean(v.recordsFrom || v.recordsTo || v.until);
      if (!chosen) {
        ctx.addIssue({
          code: 'custom',
          path: ['sharing'],
          message: 'Choose what the patient agreed to share, or share the whole record.',
        });
      }
      if (v.shareWith.length > CONSENT_LIMITS.partners) {
        ctx.addIssue({
          code: 'custom',
          path: ['shareWith'],
          message: `Choose at most ${CONSENT_LIMITS.partners} accounts.`,
        });
      }
      if (v.recordsFrom && v.recordsTo && v.recordsFrom > v.recordsTo) {
        ctx.addIssue({ code: 'custom', path: ['recordsTo'], message: 'Choose a day on or after the first day.' });
      }
      if (v.until && v.until < todayIso()) {
        ctx.addIssue({ code: 'custom', path: ['until'], message: 'Choose today or a later day.' });
      }
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

/**
 * A given record-sharing consent for the whole record (no recipient, purpose, kinds or period) with no signed form, as
 * this form records it: a second member of staff witnesses it at the screen (MAKER-CHECKER.md, slice 14).
 */
export function needsWitness(v: Pick<ConsentFormValues, 'type' | 'decision' | 'sharing'>): boolean {
  return v.type === 'DataSharing' && v.decision === 'given' && v.sharing === 'everything';
}

/** A given record-sharing consent that the patient limited: only then does the form ask what it covers. */
export function limitsSharing(v: Pick<ConsentFormValues, 'type' | 'decision' | 'sharing'>): boolean {
  return v.type === 'DataSharing' && v.decision === 'given' && v.sharing === 'limited';
}

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
    sharing: 'everything',
    shareWith: [],
    purposes: [],
    kinds: [],
    recordsFrom: null,
    recordsTo: null,
    until: null,
  };
}

const clean = (value: string): string | null => value.trim() || null;

/**
 * The request the server expects; call it only with values the schema accepted. A whole-record sharing consent carries
 * its witness's confirmation (`witnessConfirmationId`), and the witness's name then comes from it, not from what was typed.
 */
export function toCaptureRequest(
  v: ConsentFormValues,
  witnessConfirmationId: string | null = null,
): CaptureConsentRequest {
  if (!v.type) {
    throw new Error('The consent type is required.');
  }
  const proxy = v.signer === 'other';
  return {
    type: v.type,
    consentVersion: clean(v.consentVersion),
    granted: v.decision === 'given',
    witnessName: needsWitness(v) ? null : clean(v.witnessName),
    signedByRelation: proxy ? clean(v.signedByRelation) : null,
    signedByName: proxy ? clean(v.signedByName) : null,
    documentFileId: null,
    notes: clean(v.notes),
    sharingScope: limitsSharing(v)
      ? {
          shareWith: v.shareWith.length ? v.shareWith : null,
          purposes: v.purposes.length ? v.purposes : null,
          kinds: v.kinds.length ? v.kinds : null,
          recordsFrom: v.recordsFrom,
          recordsTo: v.recordsTo,
          until: v.until,
        }
      : null,
    witnessConfirmationId: needsWitness(v) ? witnessConfirmationId : null,
  };
}

const FORM_FIELDS = [
  'type',
  'signedByName',
  'signedByRelation',
  'witnessName',
  'consentVersion',
  'notes',
  'shareWith',
  'recordsTo',
  'until',
] as const;
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
  for (const [rawKey, messages] of Object.entries(error.fieldErrors)) {
    const message = messages[0] ?? error.title;
    // The scope's fields come back under it ("sharingScope.recordsTo"); the form shows them on their own.
    const key = rawKey.startsWith('sharingScope.') ? rawKey.slice('sharingScope.'.length) : rawKey;
    if ((FORM_FIELDS as readonly string[]).includes(key)) {
      result.fields.push([key as ConsentFormField, message]);
    } else {
      result.other.push(message);
    }
  }
  return result;
}
