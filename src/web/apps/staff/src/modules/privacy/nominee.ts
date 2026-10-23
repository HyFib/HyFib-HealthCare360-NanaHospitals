/**
 * The nominee form: its schema mirrors NomineeRules on the server (a name, how they are related, and a mobile number
 * or an e-mail address to reach them). Pure so it is unit-tested.
 */
import { formatPhone, isIndianMobile, normalizePhone } from '@hc360/core';
import { z } from 'zod';
import type { NomineeDto, SaveNomineeRequest } from './api';

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Offered as the relationship is typed; anything else can be written. */
export const RELATIONSHIP_SUGGESTIONS = [
  'Spouse',
  'Son',
  'Daughter',
  'Father',
  'Mother',
  'Brother',
  'Sister',
  'Friend',
  'Other relative',
] as const;

export const nomineeSchema = z
  .object({
    name: z.string().trim().min(1, 'Enter the nominee’s name.').max(200),
    relationship: z.string().trim().min(1, 'Say how the nominee is related to the patient.').max(64),
    phone: z.string().trim().max(20),
    email: z.string().trim().max(200),
  })
  .superRefine((v, ctx) => {
    if (v.phone && !isIndianMobile(v.phone)) {
      ctx.addIssue({ code: 'custom', path: ['phone'], message: 'Enter a valid 10-digit mobile number.' });
    }
    if (v.email && !EMAIL.test(v.email)) {
      ctx.addIssue({ code: 'custom', path: ['email'], message: 'Enter a valid e-mail address.' });
    }
    if (!v.phone && !v.email) {
      ctx.addIssue({
        code: 'custom',
        path: ['phone'],
        message: 'Enter the nominee’s mobile number or e-mail address, so the hospital can reach them.',
      });
    }
  });

export type NomineeValues = z.infer<typeof nomineeSchema>;

export function nomineeDefaults(nominee: NomineeDto | null): NomineeValues {
  return {
    name: nominee?.name ?? '',
    relationship: nominee?.relationship ?? '',
    phone: nominee?.phone ? formatPhone(nominee.phone) : '',
    email: nominee?.email ?? '',
  };
}

/** The request for the server; call it only with values the schema accepted. */
export function toSaveNominee(v: NomineeValues, version?: number): SaveNomineeRequest {
  const phone = v.phone.trim();
  const email = v.email.trim();
  return {
    name: v.name.trim(),
    relationship: v.relationship.trim(),
    phone: phone ? (normalizePhone(phone) ?? phone) : null,
    email: email || null,
    version,
  };
}
