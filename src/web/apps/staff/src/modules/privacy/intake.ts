/**
 * The request intake form: its schema mirrors CreateDataRequestRequestValidator on the server, so staff see the same
 * messages before sending. Pure (no React, no Fluent) so it is unit-tested.
 */
import { normalizePhone } from '@hc360/core';
import { z } from 'zod';
import type { CreateDataRequestRequest } from './api';
import {
  CORRECTABLE_FIELDS,
  MAX_CORRECTIONS,
  REQUEST_TYPES,
  REQUESTER_RELATIONS,
  STAFF_CHANNELS,
  STAFF_IDENTITY_CHECKS,
  type CorrectableField,
} from './labels';

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export const intakeSchema = z
  .object({
    /** The person is not a registered patient: name and contact instead of the patient. */
    notRegistered: z.boolean(),
    patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
    principalName: z.string().trim().max(200),
    contactPhone: z.string().trim().max(20),
    contactEmail: z.string().trim().max(200),
    type: z.enum(REQUEST_TYPES).nullable(),
    channel: z.enum(STAFF_CHANNELS),
    requestedBy: z.enum(REQUESTER_RELATIONS),
    requesterName: z.string().trim().max(200),
    details: z.string().trim().max(4000),
    corrections: z
      .array(
        z.object({
          field: z.union([z.enum(CORRECTABLE_FIELDS), z.literal('')]),
          requestedValue: z.string().trim().max(500),
        }),
      )
      .max(MAX_CORRECTIONS, `List at most ${MAX_CORRECTIONS} changes.`),
    identityCheckedNow: z.boolean(),
    identityCheck: z.enum(STAFF_IDENTITY_CHECKS).nullable(),
    identityNote: z.string().trim().max(500),
  })
  .superRefine((v, ctx) => {
    if (!v.notRegistered && !v.patient) {
      ctx.addIssue({
        code: 'custom',
        path: ['patient'],
        message: 'Choose the patient, or tick "The person is not a registered patient".',
      });
    }
    if (v.notRegistered) {
      if (!v.principalName) {
        ctx.addIssue({
          code: 'custom',
          path: ['principalName'],
          message: 'Enter the name of the person the request is about.',
        });
      }
      if (!v.contactPhone && !v.contactEmail) {
        ctx.addIssue({
          code: 'custom',
          path: ['contactPhone'],
          message: 'Enter a phone number or e-mail address to reply to.',
        });
      }
    }
    if (v.contactPhone && normalizePhone(v.contactPhone) === null) {
      ctx.addIssue({ code: 'custom', path: ['contactPhone'], message: 'Enter a valid phone number.' });
    }
    if (v.contactEmail && !EMAIL.test(v.contactEmail)) {
      ctx.addIssue({ code: 'custom', path: ['contactEmail'], message: 'Enter a valid e-mail address.' });
    }
    if (!v.type) {
      ctx.addIssue({ code: 'custom', path: ['type'], message: 'Choose what the person is asking for.' });
    }
    if (v.requestedBy !== 'Self' && !v.requesterName) {
      ctx.addIssue({
        code: 'custom',
        path: ['requesterName'],
        message: 'Enter the name of the person making the request.',
      });
    }
    if (v.type === 'Correction') {
      const seen = new Set<string>();
      v.corrections.forEach((row, index) => {
        if (!row.field) {
          ctx.addIssue({
            code: 'custom',
            path: ['corrections', index, 'field'],
            message: 'Choose a detail from the list.',
          });
        } else if (seen.has(row.field)) {
          ctx.addIssue({ code: 'custom', path: ['corrections', index, 'field'], message: 'List each detail once.' });
        } else {
          seen.add(row.field);
        }
        if (!row.requestedValue) {
          ctx.addIssue({
            code: 'custom',
            path: ['corrections', index, 'requestedValue'],
            message: 'Enter the correct value.',
          });
        }
      });
      if (v.corrections.length === 0 && !v.details) {
        ctx.addIssue({
          code: 'custom',
          path: ['details'],
          message: 'List the changes they ask for, or describe the request.',
        });
      }
    } else if (v.type && !v.details) {
      ctx.addIssue({ code: 'custom', path: ['details'], message: 'Describe the request.' });
    }
    if (v.identityCheckedNow) {
      if (!v.identityCheck) {
        ctx.addIssue({ code: 'custom', path: ['identityCheck'], message: 'Choose how you checked their identity.' });
      } else if (v.identityCheck === 'Other' && !v.identityNote) {
        ctx.addIssue({ code: 'custom', path: ['identityNote'], message: 'Say how the identity was checked.' });
      }
    }
  });

export type IntakeValues = z.infer<typeof intakeSchema>;

export function intakeDefaults(): IntakeValues {
  return {
    notRegistered: false,
    patient: null,
    principalName: '',
    contactPhone: '',
    contactEmail: '',
    type: null,
    channel: 'FrontDesk',
    requestedBy: 'Self',
    requesterName: '',
    details: '',
    corrections: [],
    identityCheckedNow: false,
    identityCheck: null,
    identityNote: '',
  };
}

const clean = (value: string): string | null => {
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
};

/** The request the server expects; call it only with values the schema accepted. */
export function toCreateRequest(v: IntakeValues): CreateDataRequestRequest {
  if (!v.type) {
    throw new Error('The request type is required.');
  }
  const corrections =
    v.type === 'Correction'
      ? v.corrections
          .filter((row): row is { field: CorrectableField; requestedValue: string } => row.field !== '')
          .map((row) => ({ field: row.field, requestedValue: row.requestedValue.trim() }))
      : [];
  const phone = clean(v.contactPhone);
  return {
    type: v.type,
    channel: v.channel,
    patientId: v.notRegistered ? null : (v.patient?.id ?? null),
    principalName: v.notRegistered ? clean(v.principalName) : null,
    contactPhone: phone ? (normalizePhone(phone) ?? phone) : null,
    contactEmail: clean(v.contactEmail),
    requestedBy: v.requestedBy,
    requesterName: v.requestedBy === 'Self' ? null : clean(v.requesterName),
    details: clean(v.details),
    corrections: corrections.length > 0 ? corrections : null,
    identityCheck: v.identityCheckedNow ? v.identityCheck : null,
    identityNote: v.identityCheckedNow ? clean(v.identityNote) : null,
  };
}

/** Form field for a server validation key ("patientId" is chosen with the patient picker). */
export function intakeFieldFor(serverField: string): string | null {
  const path = serverField.replace(/\[(\d+)\]/g, '.$1').replace(/^\./, '');
  if (path === 'patientId') {
    return 'patient';
  }
  const root = path.split('.')[0] ?? '';
  const known = [
    'principalName',
    'contactPhone',
    'contactEmail',
    'type',
    'channel',
    'requestedBy',
    'requesterName',
    'details',
    'identityCheck',
    'identityNote',
  ];
  if (known.includes(path)) {
    return path;
  }
  // A single change ("corrections.0.field"); list-wide messages ("corrections") have no field of their own.
  return root === 'corrections' && /^corrections\.\d+\.(field|requestedValue)$/.test(path) ? path : null;
}
