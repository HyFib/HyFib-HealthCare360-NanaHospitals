/*
 * The offline copy (read-only offline mode): the latest pre-downtime pack and the user's worklists, kept on this
 * computer so that when HC360 cannot be reached the ward can still read who is in which bed, what is due and what is
 * pending.
 *
 * It is kept encrypted: AES-GCM under a key derived (HKDF) from the key the server gives the signed-in user
 * (AuthResult.draftKey), with the user and the slot as associated data, so one user's copy cannot be read as another's
 * and one slot cannot stand in for another. The computer's storage (IndexedDB) holds only ciphertext. The derived key
 * is held in memory and in this tab's sessionStorage, so a reload of the tab while the server is down still opens it;
 * closing the tab or the app forgets it, and then the paper pack is the copy. Everything is wiped at sign-out.
 */

export interface SealedRecord {
  v: 1;
  iv: string;
  data: string;
  /** When the copy was taken (ms since the epoch), kept in clear so its age shows before it is opened. */
  at: number;
}

/** Where sealed records are kept: IndexedDB in the browser, a map in tests. */
export interface VaultBackend {
  get(slot: string): Promise<SealedRecord | undefined>;
  put(slot: string, record: SealedRecord): Promise<void>;
  clear(): Promise<void>;
}

const DB_NAME = 'hc360-offline';
const STORE = 'copies';
const KEY_SESSION = 'hc360:offline-key';
const INFO = 'hc360-offline-v1';
const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** The browser's IndexedDB, or null where there is none (then nothing is kept on the computer). */
export function indexedDbBackend(): VaultBackend | null {
  if (typeof indexedDB === 'undefined') {
    return null;
  }
  const open = () =>
    new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, 1);
      request.onupgradeneeded = () => request.result.createObjectStore(STORE);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  const run = async <T>(mode: IDBTransactionMode, work: (store: IDBObjectStore) => IDBRequest<T>) => {
    const db = await open();
    try {
      return await new Promise<T>((resolve, reject) => {
        const request = work(db.transaction(STORE, mode).objectStore(STORE));
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
    } finally {
      db.close();
    }
  };
  return {
    get: (slot) =>
      run<SealedRecord | undefined>('readonly', (s) => s.get(slot) as IDBRequest<SealedRecord | undefined>),
    put: async (slot, record) => {
      await run('readwrite', (s) => s.put(record, slot));
    },
    clear: async () => {
      await run('readwrite', (s) => s.clear());
    },
  };
}

/** A map, for tests and for pages without IndexedDB. */
export function memoryBackend(): VaultBackend & { records: Map<string, SealedRecord> } {
  const records = new Map<string, SealedRecord>();
  return {
    records,
    get: async (slot) => records.get(slot),
    put: async (slot, record) => {
      records.set(slot, record);
    },
    clear: async () => {
      records.clear();
    },
  };
}

/** Derives the offline key from the server's key for the user (HKDF-SHA-256, salted with the user's id). */
export async function deriveOfflineKey(serverKey: string, userId: string): Promise<CryptoKey> {
  const base = await crypto.subtle.importKey('raw', fromBase64(serverKey), 'HKDF', false, ['deriveKey']);
  return crypto.subtle.deriveKey(
    { name: 'HKDF', hash: 'SHA-256', salt: encoder.encode(userId), info: encoder.encode(INFO) },
    base,
    { name: 'AES-GCM', length: 256 },
    true,
    ['encrypt', 'decrypt'],
  );
}

export async function seal(key: CryptoKey, aad: string, value: unknown, at: number): Promise<SealedRecord> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv, additionalData: encoder.encode(aad) },
    key,
    encoder.encode(JSON.stringify(value)),
  );
  return { v: 1, iv: toBase64(iv), data: toBase64(new Uint8Array(data)), at };
}

/** The value, or null when the record is not one this key sealed for this user and slot. */
export async function unseal<T>(key: CryptoKey, aad: string, record: SealedRecord | undefined): Promise<T | null> {
  if (!record || record.v !== 1) {
    return null;
  }
  try {
    const plain = await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv: fromBase64(record.iv), additionalData: encoder.encode(aad) },
      key,
      fromBase64(record.data),
    );
    return JSON.parse(decoder.decode(plain)) as T;
  } catch {
    return null;
  }
}

/** Keeps the derived key for this tab only, so a reload while the server is down can still open the copy. */
export async function rememberKeyForTab(key: CryptoKey): Promise<void> {
  try {
    const raw = new Uint8Array(await crypto.subtle.exportKey('raw', key));
    sessionStorage.setItem(KEY_SESSION, toBase64(raw));
  } catch {
    // No sessionStorage: the key stays in memory only.
  }
}

export async function keyFromTab(): Promise<CryptoKey | null> {
  try {
    const raw = sessionStorage.getItem(KEY_SESSION);
    return raw ? await crypto.subtle.importKey('raw', fromBase64(raw), 'AES-GCM', true, ['encrypt', 'decrypt']) : null;
  } catch {
    return null;
  }
}

export function forgetKeyForTab(): void {
  try {
    sessionStorage.removeItem(KEY_SESSION);
  } catch {
    // Nothing kept.
  }
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
