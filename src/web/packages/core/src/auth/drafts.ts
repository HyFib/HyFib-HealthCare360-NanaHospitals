import { create } from 'zustand';

/*
 * Drafts (SRS DED-10): what a user has typed into a form and not yet saved, kept so that a WiFi drop, a crash or an
 * accidental close never loses it.
 *
 * Forms read and write them in memory. They are kept on this computer encrypted (audit P10): AES-GCM, under a key the
 * server gives each signed-in user (AuthResult.draftKey), with the draft's storage key as associated data so one draft
 * cannot stand in for another. The key is the same in each of the user's sessions, so a draft typed before a crash or an
 * outage opens again once they sign in again. It never leaves memory: the computer holds only ciphertext, which someone
 * else signed in on the same Windows account cannot read. Until the key comes (a locked session, or a page without
 * WebCrypto) drafts are kept in memory only.
 */

const DRAFT_PREFIX = 'hc360:draft:';

/** Where a user's draft of one form is kept. */
export function draftStorageKey(userId: string, key: string): string {
  return `${DRAFT_PREFIX}${userId}:${key}`;
}

/** A draft as the computer holds it. */
interface SealedDraft {
  v: 2;
  iv: string;
  data: string;
}

interface OpenDrafts {
  userId: string;
  key: Promise<CryptoKey | null>;
}

let current: OpenDrafts | null = null;
const memory = new Map<string, string>();
const encoder = new TextEncoder();
const decoder = new TextDecoder();

/** Whose drafts are open: a form opened before they were looks again once they are. */
export const useDrafts = create<{ openFor: string | null }>(() => ({ openFor: null }));

/**
 * The drafts as forms use them, like localStorage: read from memory, and kept on the computer encrypted once the user's
 * key has come.
 */
export const draftStorage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> = {
  getItem: (storageKey) => memory.get(storageKey) ?? null,
  setItem: (storageKey, value) => {
    memory.set(storageKey, value);
    void keep(storageKey, value);
  },
  removeItem: (storageKey) => {
    memory.delete(storageKey);
    // Until the user's drafts are open, the one kept on the computer has not been read: a form that found none in
    // memory may not take it away.
    const openFor = useDrafts.getState().openFor;
    if (openFor === null || !storageKey.startsWith(draftStorageKey(openFor, ''))) {
      return;
    }
    try {
      localStorage.removeItem(storageKey);
    } catch {
      // Storage unavailable: nothing to remove.
    }
  },
};

/**
 * Opens the user's drafts with the key the server gave them. Those kept on the computer are read into memory, any an
 * older HC360 kept unencrypted are kept again encrypted, and so are any typed before the key came. Another user's
 * drafts, if open, are closed first.
 */
export async function openDrafts(userId: string, draftKey: string): Promise<void> {
  if (current?.userId === userId) {
    return;
  }
  if (current) {
    closeDrafts();
  }
  const drafts: OpenDrafts = { userId, key: importKey(draftKey) };
  current = drafts;
  const key = await drafts.key;
  if (current !== drafts) {
    return;
  }
  if (!key) {
    // Without WebCrypto the kept drafts cannot be read, so they stay closed; new ones are kept in memory only.
    return;
  }
  const prefix = draftStorageKey(userId, '');
  for (const storageKey of storedKeys(prefix)) {
    // One typed since the app started is newer than the one kept.
    if (memory.has(storageKey)) {
      continue;
    }
    const value = await unseal(key, storageKey, stored(storageKey));
    if (current !== drafts) {
      return;
    }
    if (value !== null && !memory.has(storageKey)) {
      memory.set(storageKey, value);
    }
  }
  for (const [storageKey, value] of memory) {
    if (storageKey.startsWith(prefix)) {
      void keep(storageKey, value);
    }
  }
  useDrafts.setState({ openFor: userId });
}

/** Forgets the key and the drafts in memory. Those kept on the computer stay, for their user's next sign-in. */
export function closeDrafts(): void {
  current = null;
  memory.clear();
  useDrafts.setState({ openFor: null });
}

/** Removes every user's drafts, from memory and from the computer: at sign-out, or when the server ends the session. */
export function clearDrafts(): void {
  closeDrafts();
  for (const storageKey of storedKeys(DRAFT_PREFIX)) {
    try {
      localStorage.removeItem(storageKey);
    } catch {
      // Storage unavailable: nothing to clear.
    }
  }
}

/** Keeps a draft on the computer encrypted, unless a newer one replaced it meanwhile or the drafts were closed. */
async function keep(storageKey: string, value: string): Promise<void> {
  const drafts = current;
  const key = drafts && storageKey.startsWith(draftStorageKey(drafts.userId, '')) ? await drafts.key : null;
  if (!key) {
    return;
  }
  try {
    const sealed = await seal(key, storageKey, value);
    if (current === drafts && memory.get(storageKey) === value) {
      localStorage.setItem(storageKey, JSON.stringify(sealed));
    }
  } catch {
    // Quota or private mode: the draft is kept in memory only.
  }
}

async function seal(key: CryptoKey, storageKey: string, value: string): Promise<SealedDraft> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv, additionalData: encoder.encode(storageKey) },
    key,
    encoder.encode(value),
  );
  return { v: 2, iv: toBase64(iv), data: toBase64(new Uint8Array(data)) };
}

/** A kept draft, decrypted, or as an older HC360 kept it, unencrypted. Null when it cannot be read with this key. */
async function unseal(key: CryptoKey, storageKey: string, kept: string | null): Promise<string | null> {
  if (!kept) {
    return null;
  }
  try {
    const sealed = JSON.parse(kept) as Partial<SealedDraft> | null;
    if (sealed?.v !== 2) {
      return kept;
    }
    const value = await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv: fromBase64(sealed.iv ?? ''), additionalData: encoder.encode(storageKey) },
      key,
      fromBase64(sealed.data ?? ''),
    );
    return decoder.decode(value);
  } catch {
    return null;
  }
}

async function importKey(draftKey: string): Promise<CryptoKey | null> {
  try {
    return await crypto.subtle.importKey('raw', fromBase64(draftKey), 'AES-GCM', false, ['encrypt', 'decrypt']);
  } catch {
    // No WebCrypto (a page not served over HTTPS), or not a key: drafts are kept in memory only.
    return null;
  }
}

/** Another of the app's windows kept or removed one of the user's drafts: the same here. */
async function fromAnotherWindow(event: StorageEvent): Promise<void> {
  const drafts = current;
  if (!drafts || (event.key !== null && !event.key.startsWith(draftStorageKey(drafts.userId, '')))) {
    return;
  }
  if (event.key === null) {
    // The other window cleared everything it keeps.
    memory.clear();
    return;
  }
  if (event.newValue === null) {
    memory.delete(event.key);
    return;
  }
  const key = await drafts.key;
  const value = key ? await unseal(key, event.key, event.newValue) : null;
  if (value !== null && current === drafts) {
    memory.set(event.key, value);
  }
}

if (typeof window !== 'undefined') {
  window.addEventListener('storage', (event) => void fromAnotherWindow(event));
}

function storedKeys(prefix: string): string[] {
  const keys: string[] = [];
  try {
    for (let i = 0; i < localStorage.length; i++) {
      const storageKey = localStorage.key(i);
      if (storageKey?.startsWith(prefix)) {
        keys.push(storageKey);
      }
    }
  } catch {
    // Storage unavailable: nothing kept.
  }
  return keys;
}

function stored(storageKey: string): string | null {
  try {
    return localStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

function toBase64(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary);
}

function fromBase64(text: string): Uint8Array<ArrayBuffer> {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}
