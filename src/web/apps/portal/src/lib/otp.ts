/**
 * Sign-in helpers (pure functions, unit-tested). The server allows 3 codes per number and 5 requests per device in
 * 15 minutes, so the resend button backs off (30 s, 60 s, 2 min, then 5 min) instead of letting people hit the limit.
 */

export const OTP_LENGTH = 6;

/** Keeps only the 10 national digits of an Indian mobile number typed or pasted in any common form. */
export function sanitizePhoneInput(input: string): string {
  let digits = input.replace(/\D/g, '');
  if (digits.length > 10 && digits.startsWith('91') && input.trim().startsWith('+')) {
    digits = digits.slice(2);
  } else if (digits.length === 12 && digits.startsWith('91')) {
    digits = digits.slice(2);
  } else if (digits.length === 11 && digits.startsWith('0')) {
    digits = digits.slice(1);
  }
  return digits.slice(0, 10);
}

/** A mobile number the hospital's SMS can reach: 10 digits starting with 6, 7, 8 or 9. */
export function isValidMobile(digits: string): boolean {
  return /^[6-9]\d{9}$/.test(digits);
}

/** E.164 for the server ("+91XXXXXXXXXX"). */
export function toE164(digits: string): string {
  return `+91${digits}`;
}

/** Only digits, at most six (works with SMS autofill and pasting "Your code is 123456"). */
export function sanitizeCode(input: string): string {
  return input.replace(/\D/g, '').slice(0, OTP_LENGTH);
}

/** Seconds to wait before another code may be requested, after `sent` codes in this sign-in. */
export function resendCooldownSeconds(sent: number): number {
  const steps = [30, 60, 120];
  return sent <= 0 ? 0 : (steps[sent - 1] ?? 300);
}

/** Seconds to wait after the server said "too many codes" without saying until when. */
export const THROTTLED_COOLDOWN_SECONDS = 300;

/**
 * Seconds to wait after the server said "too many codes": until the time it gives (`data.retryAfterSeconds`, when the
 * number's third latest code leaves its 15 minutes), else the fixed wait. Relative seconds, so a phone whose clock is
 * wrong still counts down to the right moment.
 */
export function throttledWaitSeconds(data: unknown): number {
  const seconds =
    data !== null && typeof data === 'object' && 'retryAfterSeconds' in data ? Number(data.retryAfterSeconds) : NaN;
  return Number.isFinite(seconds) && seconds > 0 ? Math.min(Math.ceil(seconds), 15 * 60) : THROTTLED_COOLDOWN_SECONDS;
}

const DEFAULT_LIFETIME_MS = 5 * 60_000;
const MAX_LIFETIME_MS = 15 * 60_000;

/**
 * When the code stops working, on this device's clock. The server sends an absolute expiry; if the phone's clock is
 * wrong (common on cheap phones), that would show a nonsense countdown, so an implausible lifetime falls back to the
 * standard five minutes counted from when the code was sent.
 */
export function otpDeadline(expiresAtIso: string | null | undefined, receivedAtMs: number): number {
  const expires = expiresAtIso ? Date.parse(expiresAtIso) : Number.NaN;
  const lifetime = expires - receivedAtMs;
  return Number.isFinite(lifetime) && lifetime > 0 && lifetime <= MAX_LIFETIME_MS
    ? expires
    : receivedAtMs + DEFAULT_LIFETIME_MS;
}

/** Whole seconds left until `deadlineMs` (never negative). */
export function secondsUntil(deadlineMs: number, nowMs: number): number {
  return Math.max(0, Math.ceil((deadlineMs - nowMs) / 1000));
}
