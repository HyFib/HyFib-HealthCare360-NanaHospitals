/**
 * Family members on other mobile numbers (PP-01): the add form's checks (the server's, so a patient hears about a
 * mistake before a code is spent) and the server's refusals in the patient's language. Pure, so it is unit-tested.
 */
import { isApiError } from '@hc360/core';
import type { MessageKey } from '@/i18n';
import { isValidMobile, sanitizePhoneInput, toE164 } from '@/lib/otp';
import type { FamilyRelation, RequestFamilyCode } from '@/lib/types';

export const FAMILY_RELATIONS: readonly FamilyRelation[] = [
  'Parent',
  'Child',
  'Spouse',
  'Sibling',
  'Grandparent',
  'Grandchild',
  'Other',
];

/** The relative as a noun ("parent", "husband or wife"), to complete "Your …" and "They are your …". */
export const RELATION_KEY: Record<FamilyRelation, MessageKey> = {
  Parent: 'family.relation.Parent',
  Child: 'family.relation.Child',
  Spouse: 'family.relation.Spouse',
  Sibling: 'family.relation.Sibling',
  Grandparent: 'family.relation.Grandparent',
  Grandchild: 'family.relation.Grandchild',
  Other: 'family.relation.Other',
};

export const UHID_MAX = 20;
export const CODE_LENGTH = 6;

export interface AddFamilyValues {
  uhid: string;
  /** As typed; only the 10 national digits are kept. */
  phone: string;
  relation: FamilyRelation | '';
}

export const emptyAddFamily = (): AddFamilyValues => ({ uhid: '', phone: '', relation: '' });

export interface AddFamilyProblem {
  path: keyof AddFamilyValues;
  key: MessageKey;
}

export function checkAddFamily(values: AddFamilyValues): AddFamilyProblem[] {
  const problems: AddFamilyProblem[] = [];
  const uhid = values.uhid.trim();
  if (!uhid || uhid.length > UHID_MAX || !/^[A-Za-z0-9-]+$/.test(uhid)) {
    problems.push({ path: 'uhid', key: 'family.add.uhid.invalid' });
  }
  if (!isValidMobile(sanitizePhoneInput(values.phone))) {
    problems.push({ path: 'phone', key: 'family.add.phone.invalid' });
  }
  if (!values.relation) {
    problems.push({ path: 'relation', key: 'family.add.relation.required' });
  }
  return problems;
}

/** The request the server expects; call it only with values `checkAddFamily` accepted. */
export function toCodeRequest(values: AddFamilyValues): RequestFamilyCode {
  if (!values.relation) {
    throw new Error('The relation is required.');
  }
  return {
    uhid: values.uhid.trim().toUpperCase(),
    phone: toE164(sanitizePhoneInput(values.phone)),
    relation: values.relation,
  };
}

/** The last four digits of the number typed, to remind the patient where the code went without repeating it all. */
export function phoneEnding(phone: string): string {
  return sanitizePhoneInput(phone).slice(-4);
}

/**
 * The server's refusals that the patient can act on, in their language (its own messages are English). Null for
 * anything else: the caller then uses `describeError`.
 */
export function familyErrorKey(error: unknown): MessageKey | null {
  if (!isApiError(error)) {
    return null;
  }
  switch (error.code) {
    case 'portal.family_code_throttled':
      return 'family.error.throttled';
    case 'portal.family_code_expired':
      return 'family.error.expired';
    case 'portal.family_already_linked':
      return 'family.error.already';
    case 'portal.family_own_number':
      return 'family.error.ownNumber';
  }
  return error.isValidation && error.fieldErrors.code ? 'family.error.wrongCode' : null;
}

/** A code that can no longer be used: the patient must ask for a new one. */
export function isCodeSpent(error: unknown): boolean {
  return isApiError(error) && error.code === 'portal.family_code_expired';
}

/** Minutes a code works on the server (PortalFamilyCode.Lifetime), used when this device's clock cannot be trusted. */
export const CODE_MINUTES = 10;

/**
 * Minutes the code works, from the server's expiry as seen on this device. A phone with a wrong clock would show a
 * nonsense figure, so an implausible one falls back to the server's standard lifetime.
 */
export function codeMinutes(expiresAtIso: string, receivedAtMs: number): number {
  const minutes = Math.round((Date.parse(expiresAtIso) - receivedAtMs) / 60_000);
  return Number.isFinite(minutes) && minutes >= 1 && minutes <= 15 ? minutes : CODE_MINUTES;
}
