/**
 * Sharing a patient's records with a family member's patient portal account (PP-01): the share form's rules mirror
 * LinkFamilyMemberRequestValidator on the server, and the words staff see for each link. Pure (no React, no Fluent)
 * so it is unit-tested.
 */
import { formatDateTime, formatPhone, isApiError, isIndianMobile, normalizePhone } from '@hc360/core';
import { z } from 'zod';
import {
  FAMILY_RELATIONS,
  type FamilyRelation,
  type LinkFamilyMemberRequest,
  type PatientPortalAccessDto,
  type PortalFamilyLinkDto,
  type PortalLinkChannel,
  type PortalLinkDto,
  type PortalLinkSent,
  type PortalLinkState,
} from './api';

/** The server keeps 200 characters for who agreed, and 300 for why a link ended. */
export const PORTAL_ACCESS_LIMITS = { guardianName: 180, reason: 300 } as const;

/** Completes "The patient is the family member's …". */
export const relationLabel: Record<FamilyRelation, string> = {
  Parent: 'parent',
  Child: 'child',
  Spouse: 'husband or wife',
  Sibling: 'brother or sister',
  Grandparent: 'grandparent',
  Grandchild: 'grandchild',
  Other: 'relative',
};

export const RELATION_OPTIONS = FAMILY_RELATIONS.map((value) => ({ value, label: relationLabel[value] }));

/** What the patient's own number gives, at the top of the panel. */
export function ownNumberText(access: PatientPortalAccessDto): string {
  return access.ownPhone
    ? `Anyone who signs in to the patient portal with the patient’s own number, ${formatPhone(access.ownPhone)}, sees these records.`
    : 'The patient has no mobile number, so no portal account sees these records through its own number.';
}

/** How the link was made, and who agreed. */
export function linkSourceText(link: PortalFamilyLinkDto): string {
  return link.source === 'FrontDesk'
    ? `At the front desk${link.consentGivenBy ? `, agreed by ${link.consentGivenBy}` : ''}`
    : `In the portal, with a code sent to ${formatPhone(link.codeSentTo)}`;
}

/** When the link ended, by whom; null while it is in force. */
export function endedText(link: PortalFamilyLinkDto): string | null {
  if (!link.revokedAt) {
    return null;
  }
  return `Ended ${formatDateTime(link.revokedAt)}${link.revokedByName ? ` by ${link.revokedByName}` : ''}`;
}

export function shareFormSchema(ownPhone: string | null) {
  return z
    .object({
      accountPhone: z.string().trim(),
      relation: z.enum(FAMILY_RELATIONS).nullable(),
      /** The patient agreed, or a guardian agreed for a child or a patient who cannot. */
      agreedBy: z.enum(['patient', 'guardian']),
      guardianName: z
        .string()
        .trim()
        .max(PORTAL_ACCESS_LIMITS.guardianName, `Use at most ${PORTAL_ACCESS_LIMITS.guardianName} characters.`),
      identityChecked: z.boolean(),
    })
    .superRefine((v, ctx) => {
      if (!isIndianMobile(v.accountPhone)) {
        ctx.addIssue({
          code: 'custom',
          path: ['accountPhone'],
          message: 'Enter the family member’s 10-digit mobile number.',
        });
      } else if (ownPhone && normalizePhone(v.accountPhone) === ownPhone) {
        ctx.addIssue({
          code: 'custom',
          path: ['accountPhone'],
          message: 'This is the patient’s own number: its portal account sees the records already.',
        });
      }
      if (!v.relation) {
        ctx.addIssue({ code: 'custom', path: ['relation'], message: 'Choose how the patient is related to them.' });
      }
      if (v.agreedBy === 'guardian' && !v.guardianName) {
        ctx.addIssue({ code: 'custom', path: ['guardianName'], message: 'Enter the name of the guardian who agreed.' });
      }
      if (!v.identityChecked) {
        ctx.addIssue({
          code: 'custom',
          path: ['identityChecked'],
          message: 'Check who the patient and the family member are before sharing the records.',
        });
      }
    });
}

export type ShareFormValues = z.infer<ReturnType<typeof shareFormSchema>>;

export function shareFormDefaults(): ShareFormValues {
  return { accountPhone: '', relation: null, agreedBy: 'patient', guardianName: '', identityChecked: false };
}

/** The request the server expects; call it only with values the schema accepted. */
export function toLinkRequest(v: ShareFormValues): LinkFamilyMemberRequest {
  const accountPhone = normalizePhone(v.accountPhone);
  if (!accountPhone || !v.relation) {
    throw new Error('The number and the relation are required.');
  }
  return {
    accountPhone,
    relation: v.relation,
    consentGivenBy: v.agreedBy === 'patient' ? 'the patient' : `${v.guardianName.trim()}, guardian`,
    identityChecked: v.identityChecked,
  };
}

const FORM_FIELDS = ['accountPhone', 'relation', 'identityChecked'] as const;
export type ShareFormField = (typeof FORM_FIELDS)[number];

/**
 * Where a failed share's messages belong: validation messages for the fields the form shows go on those fields, the
 * rest (the number already sees the records, a lost connection…) above the buttons.
 */
export function shareServerErrors(error: unknown): { fields: [ShareFormField, string][]; other: string[] } {
  if (!isApiError(error)) {
    return { fields: [], other: ['Please try again.'] };
  }
  if (!error.isValidation) {
    return { fields: [], other: [error.userMessage] };
  }
  const fields: [ShareFormField, string][] = [];
  const other: string[] = [];
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const message = messages[0] ?? error.title;
    if ((FORM_FIELDS as readonly string[]).includes(key)) {
      fields.push([key as ShareFormField, message]);
    } else {
      other.push(message);
    }
  }
  return { fields, other };
}

// ------------------------------------------------------------------ links to the portal sent to the patient's number

/** "SMS", "WhatsApp" or "SMS and WhatsApp". */
export function channelsText(channels: readonly PortalLinkChannel[]): string {
  const names = channels.map((c) => (c === 'Sms' ? 'SMS' : 'WhatsApp'));
  return names.length > 1 ? names.join(' and ') : (names[0] ?? '—');
}

/** The badge of a link: whether the patient can still use it. */
export const linkStateLabel: Record<PortalLinkState, string> = {
  Active: 'Not opened yet',
  Opened: 'Opened',
  Expired: 'Expired',
  Revoked: 'Stopped',
};

export const linkStateTone: Record<PortalLinkState, 'success' | 'informative' | 'subtle'> = {
  Active: 'informative',
  Opened: 'success',
  Expired: 'subtle',
  Revoked: 'subtle',
};

/** The line under a link's badge: when it was opened, until when it works, or why it stopped. */
export function linkStateDetail(link: PortalLinkDto): string {
  switch (link.state) {
    case 'Revoked':
      return link.revokedReason === 'replaced'
        ? 'A newer link replaced it'
        : link.revokedReason === 'number_changed'
          ? 'The patient’s mobile number changed'
          : `Stopped ${formatDateTime(link.revokedAt)}`;
    case 'Expired':
      return `Expired ${formatDateTime(link.expiresAt)}`;
    case 'Opened':
      return `Opened ${formatDateTime(link.openedAt)}; works until ${formatDateTime(link.expiresAt)}`;
    default:
      return `Works until ${formatDateTime(link.expiresAt)}`;
  }
}

/** What staff are told once a link went: how, to which number, until when, and why it went by SMS only. */
export function linkSentText(sent: PortalLinkSent): string {
  const text = `Sent by ${channelsText(sent.channels)} to ${sent.maskedPhone}. It works until ${formatDateTime(sent.expiresAt)}.`;
  return sent.notice ? `${text} ${sent.notice}` : text;
}
