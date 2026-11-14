/**
 * "Confirm your details" (ASVS V6.6.1): records registered with this mobile number that nobody has verified yet wait for
 * the patient's date of birth or the UHID on their hospital card. The form's checks (the server's, so a patient hears
 * about a mistake before one of the day's five tries is spent) and the server's refusals in the patient's language. Pure,
 * so it is unit-tested.
 */
import { isApiError } from '@hc360/core';
import type { MessageKey } from '@/i18n';
import type { ConfirmRecordsRequest } from '@/lib/types';

export type ConfirmMethod = 'dateOfBirth' | 'uhid';

export interface ConfirmValues {
  method: ConfirmMethod;
  /** The date of birth as typed, day, month and year apart: easier on a phone than a calendar decades back. */
  day: string;
  month: string;
  year: string;
  uhid: string;
}

export const emptyConfirm = (): ConfirmValues => ({ method: 'dateOfBirth', day: '', month: '', year: '', uhid: '' });

export const UHID_MAX = 20;

/** The date typed as the server wants it ("1979-06-23"), or null when it is not a date on the calendar. */
export function dateOfBirthOf(day: string, month: string, year: string): string | null {
  const [d, m, y] = [day.trim(), month.trim(), year.trim()];
  if (!/^\d{1,2}$/.test(d) || !/^\d{1,2}$/.test(m) || !/^\d{4}$/.test(y)) {
    return null;
  }
  const date = new Date(Date.UTC(Number(y), Number(m) - 1, Number(d)));
  if (date.getUTCFullYear() !== Number(y) || date.getUTCMonth() !== Number(m) - 1 || date.getUTCDate() !== Number(d)) {
    return null;
  }
  return `${y}-${m.padStart(2, '0')}-${d.padStart(2, '0')}`;
}

export interface ConfirmProblem {
  path: 'dateOfBirth' | 'uhid';
  key: MessageKey;
}

/** What is wrong with the details before they are sent; `today` is the hospital's date ("2026-11-14"). */
export function checkConfirm(values: ConfirmValues, today: string): ConfirmProblem[] {
  if (values.method === 'uhid') {
    const uhid = values.uhid.trim();
    return !uhid || uhid.length > UHID_MAX || !/^[A-Za-z0-9-]+$/.test(uhid)
      ? [{ path: 'uhid', key: 'confirm.uhid.invalid' }]
      : [];
  }
  const born = dateOfBirthOf(values.day, values.month, values.year);
  if (!born) {
    return [{ path: 'dateOfBirth', key: 'confirm.dob.invalid' }];
  }
  return born > today || born < '1900-01-01' ? [{ path: 'dateOfBirth', key: 'confirm.dob.past' }] : [];
}

/** The request the server expects; call it only with values `checkConfirm` accepted. */
export function toConfirmRequest(values: ConfirmValues): ConfirmRecordsRequest {
  return values.method === 'uhid'
    ? { dateOfBirth: null, uhid: values.uhid.trim().toUpperCase() }
    : { dateOfBirth: dateOfBirthOf(values.day, values.month, values.year), uhid: null };
}

/** The server's refusals, in the portal's own words: none for anything else (the general error words then). */
export function confirmErrorKey(error: unknown): MessageKey | null {
  if (!isApiError(error)) {
    return null;
  }
  switch (error.code) {
    case 'portal.confirm_no_match':
      return 'confirm.error.noMatch';
    case 'portal.confirm_throttled':
      return 'confirm.error.throttled';
    case 'portal.confirm_nothing':
      return 'confirm.error.nothing';
    default:
      return null;
  }
}

/** A number a refusal carries in its data (`attemptsLeft`, `retryAfterSeconds`), or undefined. */
export function refusalNumber(error: unknown, name: 'attemptsLeft' | 'retryAfterSeconds'): number | undefined {
  if (!isApiError(error) || typeof error.data !== 'object' || error.data === null) {
    return undefined;
  }
  const value = Number((error.data as Record<string, unknown>)[name]);
  return Number.isFinite(value) ? value : undefined;
}

/** How long to wait, as a patient reads it: hours over an hour, minutes over a minute, rounded up. */
export function waitOf(seconds: number): { key: MessageKey; count: number } {
  if (seconds > 3600) {
    return { key: 'confirm.wait.hours', count: Math.ceil(seconds / 3600) };
  }
  return seconds > 60
    ? { key: 'confirm.wait.minutes', count: Math.ceil(seconds / 60) }
    : { key: 'confirm.wait.minute', count: 1 };
}
