import { isApiError, isIndianMobile, normalizePhone } from '@hc360/core';
import type { PhoneChangeConfirmed, PhoneVerified } from './api';

// Changing a patient's registered mobile number (PatientPhoneChangeService): a code goes by SMS to the new number, and
// the number changes when the code the patient reads out is entered. The rules the dialog checks before asking, and how
// it reads the server's refusals.

/** The server's message for a number that is not an Indian mobile number (PatientPhoneChange.IndianMobileMessage). */
export const NEW_PHONE_MESSAGE =
  'Enter a 10-digit Indian mobile number: the patient gets messages and signs in to the portal with it. Put any other number in Alternate phone.';

export const SAME_PHONE_MESSAGE = 'The patient is registered with this number already.';

/** Why no code can go to `input`, or null when one can: not an Indian mobile number, or the number on the record. */
export function newPhoneProblem(input: string, current: string | null): string | null {
  if (!isIndianMobile(input)) {
    return NEW_PHONE_MESSAGE;
  }
  return current !== null && normalizePhone(input) === current ? SAME_PHONE_MESSAGE : null;
}

/** A refusal as the dialog shows it. */
export interface PhoneChangeProblem {
  /** The field it belongs under, or null for one about the change as a whole. */
  field: 'phone' | 'code' | null;
  message: string;
  /** Seconds until the number may be sent another code (three in 15 minutes). */
  retryAfterSeconds?: number;
  /** Tries left with this code, after a wrong one. */
  attemptsLeft?: number;
  /** The code can no longer be used: expired, used up, replaced, or the record's number changed meanwhile. */
  spent: boolean;
}

const SPENT = new Set([
  'patient.phone_code_expired',
  'patient.phone_changed_meanwhile',
  'patient.phone_change_not_found',
  // A verification of the number on the record (VerifyPhoneDialog) is refused the same way.
  'patient.phone_verification_not_found',
]);

export function phoneChangeProblem(error: unknown): PhoneChangeProblem {
  if (!isApiError(error)) {
    return { field: null, message: 'The server could not be reached. Try again.', spent: false };
  }
  const data = (typeof error.data === 'object' && error.data !== null ? error.data : {}) as Record<string, unknown>;
  const codeMessage = error.fieldErrors.code?.[0];
  if (codeMessage) {
    const left = Number(data.attemptsLeft);
    return {
      field: 'code',
      message: codeMessage,
      attemptsLeft: Number.isFinite(left) ? left : undefined,
      spent: left === 0,
    };
  }
  const phoneMessage = error.fieldErrors.phone?.[0];
  if (phoneMessage) {
    return { field: 'phone', message: phoneMessage, spent: false };
  }
  if (error.code === 'patient.phone_code_throttled') {
    const wait = Number(data.retryAfterSeconds);
    return {
      field: null,
      message: error.userMessage,
      retryAfterSeconds: Number.isFinite(wait) && wait > 0 ? Math.ceil(wait) : undefined,
      spent: false,
    };
  }
  return { field: null, message: error.userMessage, spent: SPENT.has(error.code ?? '') };
}

/** A wait as minutes and seconds: 245 → "4:05". */
export function formatWait(seconds: number): string {
  const whole = Math.max(0, Math.ceil(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

/** What the desk is told once the number changed, and what changed with it. */
export function describeChange(done: PhoneChangeConfirmed): string {
  const moved = done.familyLinksMoved;
  return [
    `The patient’s mobile number is now ${done.maskedPhone}. Portal links sent to the old number no longer work.`,
    moved > 0
      ? `The portal on the new number sees the ${moved === 1 ? 'family member' : `${moved} family members`} the old number saw.`
      : null,
    done.oldNumberSignedOut ? 'The portal on the old number reaches no patient now, and was signed out.' : null,
  ]
    .filter(Boolean)
    .join(' ');
}

/** What the desk is told once the number on the record is verified (VerifyPhoneDialog). */
export function describeVerified(done: PhoneVerified): string {
  return `${done.maskedPhone} is verified as the patient’s mobile number. Reports and bills can go to it, and the patient portal shows the record to it.`;
}
