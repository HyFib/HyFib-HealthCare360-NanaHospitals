import { isApiError } from '@hc360/core';
import { api } from '@/lib/api';
import { solve } from './solver';

/**
 * The anti-automation check on the portal's sign-in and feedback (SRS SEC-06, SECURITY.md "Anti-automation"). The
 * server hands out a signed challenge (`POST /api/v1/public/challenge`); the browser solves it in a worker and sends the
 * answer in `X-HC360-Proof` with the request. There is no puzzle to read or tap, so it works the same with a screen
 * reader; the page says "Checking your browser…" only when it takes longer than {@link SLOW_AFTER_MS}.
 */

/** The kinds of request the server issues challenges for; each challenge is good for its own kind only. */
export type ProofAction = 'otp' | 'feedback';

export const PROOF_HEADER = 'X-HC360-Proof';

/** The server's one answer to a missing, wrong, spent or too easy proof. */
export const CHECK_FAILED = 'request.check_failed';

/** How long the check may take before the page says that it is checking. */
export const SLOW_AFTER_MS = 1500;

/** A challenge, or `required: false` while the hospital has the check turned off. */
export interface ProofChallenge {
  required: boolean;
  challenge: string | null;
  bits: number;
  expiresAt: string | null;
}

interface Proof {
  /** The header value, or null when none is needed. */
  value: string | null;
  /** When it stops being accepted, on this device's clock (a challenge lives 5 minutes). */
  usableUntil: number;
}

const LIFETIME_MS = 5 * 60_000;
/** A proof solved in advance is not used this close to its expiry: the request would arrive too late. */
const MARGIN_MS = 15_000;

/** Solves in a worker when the browser has them, else (old browsers, tests) on the page itself. */
function solveOffPage(challenge: string, bits: number): Promise<number> {
  if (typeof Worker === 'undefined') {
    return solve(challenge, bits);
  }
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL('./solver.worker.ts', import.meta.url), { type: 'module' });
    const finish = () => worker.terminate();
    worker.onmessage = (event: MessageEvent<{ nonce?: number; error?: string }>) => {
      finish();
      if (event.data.nonce === undefined) {
        reject(new Error(event.data.error ?? 'The check failed.'));
      } else {
        resolve(event.data.nonce);
      }
    };
    worker.onerror = (event) => {
      finish();
      // A worker that cannot start (a strict browser setting) must not stop the patient: solve here instead.
      event.preventDefault();
      solve(challenge, bits).then(resolve, reject);
    };
    worker.postMessage({ challenge, bits });
  });
}

async function createProof(action: ProofAction, subject?: string): Promise<Proof> {
  const startedAt = Date.now();
  const challenge = await api.post<ProofChallenge>('/api/v1/public/challenge', { action, subject: subject ?? null });
  if (!challenge.required || !challenge.challenge) {
    return { value: null, usableUntil: startedAt + LIFETIME_MS };
  }
  const nonce = await solveOffPage(challenge.challenge, challenge.bits);
  // Counted from when it was asked for, on this device's clock: a phone's clock may be wrong by hours.
  return { value: `${challenge.challenge}:${nonce}`, usableUntil: startedAt + LIFETIME_MS };
}

const prepared = new Map<ProofAction, Promise<Proof | null>>();

/**
 * Starts solving a challenge in the background (when a form appears), so the patient does not wait for it on submit.
 * Calling it again while one is waiting does nothing.
 */
export function prepareProof(action: ProofAction): void {
  if (!prepared.has(action)) {
    prepared.set(
      action,
      createProof(action).catch(() => null),
    );
  }
}

/** Forgets proofs solved in advance (tests, signing out). */
export function clearPreparedProofs(): void {
  prepared.clear();
}

async function takeProof(action: ProofAction, subject: string | undefined, fresh: boolean): Promise<string | null> {
  const ready = fresh ? undefined : prepared.get(action);
  prepared.delete(action);
  const proof = ready ? await ready : null;
  if (proof && proof.usableUntil - MARGIN_MS > Date.now()) {
    return proof.value;
  }
  return (await createProof(action, subject)).value;
}

async function timed<T>(work: Promise<T>, onSlow?: (slow: boolean) => void): Promise<T> {
  if (!onSlow) {
    return work;
  }
  const timer = setTimeout(() => onSlow(true), SLOW_AFTER_MS);
  try {
    return await work;
  } finally {
    clearTimeout(timer);
    onSlow(false);
  }
}

/**
 * Sends a request with a proof of work. It uses a proof solved in advance ({@link prepareProof}) when there is one; when
 * the server refuses the proof (it asks more work of this number or this connection since), it solves a fresh one,
 * for `subject` (the mobile number a code is asked for), and tries once more. A proof solved in advance cannot name the
 * number, so for a number that failed lately it is too easy; the server does not hold that refusal against the patient.
 * `onSlow(true)` is called when getting a proof takes longer than {@link SLOW_AFTER_MS}, and `onSlow(false)` when it is
 * done.
 */
export async function withProof<T>(
  action: ProofAction,
  send: (headers: Record<string, string>) => Promise<T>,
  options: { subject?: string; onSlow?: (slow: boolean) => void } = {},
): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    const proof = await timed(takeProof(action, options.subject, attempt > 1), options.onSlow);
    try {
      return await send(proof ? { [PROOF_HEADER]: proof } : {});
    } catch (error) {
      if (attempt >= 2 || !(isApiError(error) && error.code === CHECK_FAILED)) {
        throw error;
      }
    }
  }
}
