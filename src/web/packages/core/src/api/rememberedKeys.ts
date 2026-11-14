/*
 * The Idempotency-Key of a write whose outcome is not known (SRS DED-08). A write's outcome is not known when it ended
 * in a network failure or a timeout after the API client's own retries, or the server was still working on it: the
 * server may have saved it, its answer lost on the way. The same write sent again (the same user, method, path and
 * body) then carries the same key, so the server answers with the first attempt's outcome instead of saving it twice.
 * A definitive answer, and signing out, forget the key.
 *
 * The keys are kept in memory and in this tab's sessionStorage, so reloading the page keeps them, for a day: as long as
 * the server keeps its answers. Each is kept under a SHA-256 hash of the request, so the computer never holds what was
 * sent, only the hash and the key.
 */

const STORAGE_KEY = 'hc360:remembered-keys';

/** As long as the server keeps a key's answer (IdempotencyMiddleware.Retention). */
const RETENTION_MS = 24 * 60 * 60 * 1000;

/** Enough for a day of lost answers; the oldest goes first. */
const MAX_KEYS = 200;

interface RememberedKey {
  key: string;
  /** When it was remembered (milliseconds since the epoch). */
  at: number;
}

let memory: Map<string, RememberedKey> | null = null;

/**
 * The hash a write's key is remembered under: of the user, the method, the path with its query, and the body as sent.
 * Null where the browser has no WebCrypto (a page not served over HTTPS): such a write is not remembered.
 */
export async function requestFingerprint(
  userId: string,
  method: string,
  url: string,
  body: string,
): Promise<string | null> {
  try {
    const digest = await crypto.subtle.digest(
      'SHA-256',
      new TextEncoder().encode(JSON.stringify([userId, method, url, body])),
    );
    return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, '0')).join('');
  } catch {
    return null;
  }
}

/** The key remembered for the write with this fingerprint, if it is less than a day old. */
export function rememberedKey(fingerprint: string): string | undefined {
  const keys = load();
  const remembered = keys.get(fingerprint);
  if (remembered && Date.now() - remembered.at >= RETENTION_MS) {
    keys.delete(fingerprint);
    save(keys);
    return undefined;
  }
  return remembered?.key;
}

/** Remembers the key of a write whose outcome is not known, for when it is sent again. */
export function rememberKey(fingerprint: string, key: string): void {
  const keys = load();
  keys.delete(fingerprint);
  keys.set(fingerprint, { key, at: Date.now() });
  while (keys.size > MAX_KEYS) {
    const oldest = keys.keys().next().value;
    if (oldest === undefined) {
      break;
    }
    keys.delete(oldest);
  }
  save(keys);
}

/** Forgets the key of a write that got a definitive answer: the next such write is a new one. */
export function forgetKey(fingerprint: string): void {
  const keys = load();
  if (keys.delete(fingerprint)) {
    save(keys);
  }
}

/** Forgets every remembered key, from memory and from this tab: at sign-out, or when the server ends the session. */
export function forgetRememberedKeys(): void {
  memory = new Map();
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // No sessionStorage (outside a browser, or blocked): nothing kept there.
  }
}

/** The remembered keys, read from this tab's sessionStorage the first time, without those more than a day old. */
function load(): Map<string, RememberedKey> {
  if (memory) {
    return memory;
  }
  const keys = new Map<string, RememberedKey>();
  const now = Date.now();
  try {
    const kept = JSON.parse(sessionStorage.getItem(STORAGE_KEY) ?? '{}') as Record<string, Partial<RememberedKey>>;
    const entries = Object.entries(kept)
      .filter(
        (entry): entry is [string, RememberedKey] =>
          typeof entry[1].key === 'string' && typeof entry[1].at === 'number' && now - entry[1].at < RETENTION_MS,
      )
      .sort(([, a], [, b]) => a.at - b.at);
    for (const [fingerprint, remembered] of entries) {
      keys.set(fingerprint, { key: remembered.key, at: remembered.at });
    }
  } catch {
    // Nothing kept, or not readable: start empty.
  }
  memory = keys;
  return keys;
}

function save(keys: Map<string, RememberedKey>): void {
  try {
    if (keys.size === 0) {
      sessionStorage.removeItem(STORAGE_KEY);
    } else {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(Object.fromEntries(keys)));
    }
  } catch {
    // No sessionStorage, or it is full: the keys are kept in memory only.
  }
}
