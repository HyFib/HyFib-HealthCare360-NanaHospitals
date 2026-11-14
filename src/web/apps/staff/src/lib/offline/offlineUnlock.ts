import { create } from 'zustand';

/*
 * Opening the offline copy from a locked screen during an outage. Unlocking needs the server, which cannot be reached or
 * cannot reach its database, and signing out wipes the copy; so the lock screen checks the PIN or password on this
 * computer instead, and opens the copy read only while the session stays locked.
 *
 * What is kept is a verifier, never the secret. After a sign-in or unlock the server accepted, the app keeps
 * PBKDF2-HMAC-SHA512 of the PIN or password (210,000 rounds, as the server hashes them, with a random salt kept beside
 * it), sealed with HMAC-SHA-256 under a key derived (HKDF) from the key the server gives the user with the session
 * (AuthResult.draftKey). That key is held in memory only, never stored, so the verifier on the computer cannot be tried
 * against guesses away from a live session of its user, where a PIN would fall in a few thousand. One user's verifiers
 * are kept at a time: another user's key wipes them, and so do five wrong tries, as five wrong PINs end the session.
 */

export type OfflineSecret = 'pin' | 'password';

const STORAGE_KEY = 'hc360:offline-unlock';
const ITERATIONS = 210_000;
const MAX_TRIES = 5;
const INFO = 'hc360-offline-unlock-v1';
const encoder = new TextEncoder();

interface Verifier {
  salt: string;
  iterations: number;
  mac: string;
}

/** What the computer holds: the user's verifiers, and which key they were sealed under (`check`), never the key. */
interface KeptVerifiers {
  v: 1;
  userId: string;
  check: string;
  failures: number;
  pin?: Verifier;
  password?: Verifier;
}

/** The slow part of a verifier, made while the secret is in hand; sealed for a user once the server accepted it. */
export interface PreparedSecret {
  kind: OfflineSecret;
  salt: Uint8Array<ArrayBuffer>;
  stretched: ArrayBuffer;
}

export type OfflineCheck =
  { result: 'opened' } | { result: 'wrong'; triesLeft: number } | { result: 'locked-out' } | { result: 'unavailable' };

/** Whose verifiers this window can check, and for which secrets: what the lock screen offers. */
export const useOfflineUnlock = create<{ userId: string | null; secrets: OfflineSecret[] }>(() => ({
  userId: null,
  secrets: [],
}));

let sessionKey: { userId: string; key: Promise<CryptoKey | null> } | null = null;

/**
 * The key the server gave the user has come (at sign-in, refresh and unlock): verifiers are sealed and checked under
 * it. Another user's verifiers go first.
 */
export function openOfflineUnlock(userId: string, serverKey: string): void {
  const kept = read();
  if (kept && kept.userId !== userId) {
    remove();
  }
  sessionKey = { userId, key: deriveSealKey(serverKey, userId) };
  publish();
}

/** Makes the slow part of a verifier of the secret (null without WebCrypto). */
export async function prepareOfflineSecret(kind: OfflineSecret, secret: string): Promise<PreparedSecret | null> {
  try {
    const salt = crypto.getRandomValues(new Uint8Array(16));
    return { kind, salt, stretched: await stretch(secret, salt, ITERATIONS) };
  } catch {
    return null;
  }
}

/**
 * Keeps the verifier of a secret the server has just accepted from the user, in place of the one kept before. Kept only
 * under that user's key from the server, so not for a session that has none (locked, or restricted); the one kept
 * before then goes, as it may be of a secret the user has since changed.
 */
export async function keepOfflineSecret(
  userId: string,
  prepared: PreparedSecret | null | Promise<PreparedSecret | null>,
): Promise<void> {
  const secret = await prepared;
  if (!secret) {
    return;
  }
  const key = sessionKey?.userId === userId ? await sessionKey.key : null;
  if (!key || sessionKey?.userId !== userId) {
    const kept = read();
    if (kept?.userId === userId && kept[secret.kind]) {
      delete kept[secret.kind];
      write(kept);
      publish();
    }
    return;
  }
  try {
    const check = await keyCheck(key);
    const mac = await crypto.subtle.sign('HMAC', key, secret.stretched);
    const kept = read();
    const base: KeptVerifiers =
      kept?.userId === userId && kept.check === check ? kept : { v: 1, userId, check, failures: 0 };
    write({
      ...base,
      failures: 0,
      [secret.kind]: { salt: toBase64(secret.salt), iterations: ITERATIONS, mac: toBase64(new Uint8Array(mac)) },
    });
  } catch {
    // No WebCrypto or no storage: the lock screen offers no offline copy.
  }
  publish();
}

/** `prepareOfflineSecret` and `keepOfflineSecret` at once, for a secret the server accepted just now. */
export function rememberOfflineSecret(userId: string, kind: OfflineSecret, secret: string): Promise<void> {
  return keepOfflineSecret(userId, prepareOfflineSecret(kind, secret));
}

/** The secrets that can open the offline copy for the user in this window now. */
export function offlineSecrets(userId: string): OfflineSecret[] {
  const kept = read();
  if (kept?.userId !== userId || sessionKey?.userId !== userId) {
    return [];
  }
  return (['pin', 'password'] as const).filter((kind) => kept[kind] !== undefined);
}

/**
 * Checks the PIN or password on this computer, against the user's verifier. A wrong one counts; the fifth wipes the
 * verifiers, so the copy cannot be opened here until the user signs in or unlocks with the server again.
 */
export async function checkOfflineSecret(userId: string, kind: OfflineSecret, secret: string): Promise<OfflineCheck> {
  const key = sessionKey?.userId === userId ? await sessionKey.key : null;
  const kept = read();
  const verifier = kept?.userId === userId ? kept[kind] : undefined;
  if (!key || !kept || !verifier) {
    return { result: 'unavailable' };
  }
  try {
    if (kept.check !== (await keyCheck(key))) {
      // Sealed under a key the server no longer gives: it would only count the right secret as wrong.
      remove();
      publish();
      return { result: 'unavailable' };
    }
    const stretched = await stretch(secret, fromBase64(verifier.salt), verifier.iterations);
    if (await crypto.subtle.verify('HMAC', key, fromBase64(verifier.mac), stretched)) {
      write({ ...kept, failures: 0 });
      return { result: 'opened' };
    }
  } catch {
    return { result: 'unavailable' };
  }
  // Counted on what is kept now, so that wrong tries in the app's other windows count too.
  const now = read();
  if (now?.userId !== userId) {
    return { result: 'unavailable' };
  }
  const failures = now.failures + 1;
  if (failures >= MAX_TRIES) {
    remove();
    publish();
    return { result: 'locked-out' };
  }
  write({ ...now, failures });
  return { result: 'wrong', triesLeft: MAX_TRIES - failures };
}

/** Wipes the verifiers and forgets the key: at sign-out, and when the session ends while the server can be reached. */
export function forgetOfflineUnlock(): void {
  sessionKey = null;
  remove();
  publish();
}

function publish(): void {
  const kept = read();
  const userId = kept?.userId ?? null;
  useOfflineUnlock.setState({ userId, secrets: userId === null ? [] : offlineSecrets(userId) });
}

/** The key verifiers are sealed under: derived from the server's key for the user, and never extractable. */
async function deriveSealKey(serverKey: string, userId: string): Promise<CryptoKey | null> {
  try {
    const base = await crypto.subtle.importKey('raw', fromBase64(serverKey), 'HKDF', false, ['deriveKey']);
    return await crypto.subtle.deriveKey(
      { name: 'HKDF', hash: 'SHA-256', salt: encoder.encode(userId), info: encoder.encode(INFO) },
      base,
      { name: 'HMAC', hash: 'SHA-256', length: 256 },
      false,
      ['sign', 'verify'],
    );
  } catch {
    // No WebCrypto (a page not served over HTTPS), or not a key: nothing is kept, and the lock screen offers no copy.
    return null;
  }
}

async function stretch(secret: string, salt: Uint8Array<ArrayBuffer>, iterations: number): Promise<ArrayBuffer> {
  const base = await crypto.subtle.importKey('raw', encoder.encode(secret), 'PBKDF2', false, ['deriveBits']);
  return crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-512', salt, iterations }, base, 256);
}

/** Says which key verifiers were sealed under without saying anything of the key. */
async function keyCheck(key: CryptoKey): Promise<string> {
  return toBase64(new Uint8Array(await crypto.subtle.sign('HMAC', key, encoder.encode('key-check'))));
}

function read(): KeptVerifiers | null {
  try {
    const kept = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null') as KeptVerifiers | null;
    return kept?.v === 1 && typeof kept.userId === 'string' ? kept : null;
  } catch {
    return null;
  }
}

function write(kept: KeptVerifiers): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(kept));
  } catch {
    // No storage: nothing is kept.
  }
}

function remove(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // No storage: nothing was kept.
  }
}

// Another of the app's windows kept or wiped the verifiers: the lock screen here offers what is kept now.
if (typeof window !== 'undefined') {
  window.addEventListener('storage', (event) => {
    if (event.key === STORAGE_KEY || event.key === null) {
      publish();
    }
  });
}

function toBase64(bytes: Uint8Array): string {
  let text = '';
  for (const b of bytes) {
    text += String.fromCharCode(b);
  }
  return btoa(text);
}

function fromBase64(text: string): Uint8Array<ArrayBuffer> {
  const binary = atob(text);
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}
