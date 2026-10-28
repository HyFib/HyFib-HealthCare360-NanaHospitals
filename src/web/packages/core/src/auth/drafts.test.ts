// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { clearDrafts, closeDrafts, draftStorage, draftStorageKey, openDrafts, useDrafts } from './drafts';
import { auth, useSession, type AuthUser } from './session';

const asha: AuthUser = {
  id: 'u-asha',
  userName: 'asha',
  displayName: 'Asha Rao',
  type: 'staff',
  facilityId: null,
  facilityName: null,
  practitionerId: null,
  patientId: null,
  roles: ['Nurse'],
  permissions: ['patients.read'],
  facilities: [],
  mfaEnabled: false,
  hasPin: true,
  mustChangePassword: false,
  restriction: null,
};

const noteDraft = draftStorageKey('u-asha', 'nursing-note:adm-1');
const otherDraft = draftStorageKey('u-ravi', 'emr-note:enc-9');

function answer(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/**
 * SRS DED-10: drafts are kept on this computer, per user and per form, through a WiFi drop or a crash, and cleared at
 * sign-out. When the app starts again it restores the session from its cookie: a session the server says is over goes,
 * drafts and all, as at sign-out; one the server could not answer for keeps its drafts, for its user to find once
 * signed in again.
 */
describe('drafts', () => {
  beforeEach(() => {
    localStorage.setItem(noteDraft, JSON.stringify({ value: { note: 'BP 150/95, repeat in an hour' }, savedAt: 1 }));
    localStorage.setItem(otherDraft, JSON.stringify({ value: { note: 'Plan: start amlodipine' }, savedAt: 2 }));
    localStorage.setItem('hc360:theme', 'dark');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    useSession.setState({ status: 'unknown', accessToken: null, expiresAt: null, user: null, locked: false });
    localStorage.clear();
  });

  it('are kept under the user and the form they belong to', () => {
    expect(noteDraft).toBe('hc360:draft:u-asha:nursing-note:adm-1');
    expect(draftStorageKey('u-ravi', 'nursing-note:adm-1')).not.toBe(noteDraft);
    expect(draftStorageKey('u-asha', 'nursing-note:adm-2')).not.toBe(noteDraft);
  });

  it("are all cleared at sign-out, every user's, and nothing else is", async () => {
    useSession.setState({ status: 'authenticated', accessToken: 't1', user: asha });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })));

    await auth.logout();

    expect(localStorage.getItem(noteDraft)).toBeNull();
    expect(localStorage.getItem(otherDraft)).toBeNull();
    expect(localStorage.getItem('hc360:theme')).toBe('dark');
    expect(useSession.getState().status).toBe('anonymous');
  });

  it('are cleared at sign-out even when the server cannot be reached', async () => {
    useSession.setState({ status: 'authenticated', accessToken: 't1', user: asha });
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    await auth.logout();

    expect(localStorage.getItem(noteDraft)).toBeNull();
    expect(useSession.getState().status).toBe('anonymous');
  });

  it.each([
    ['answers with a server error', () => Promise.resolve(answer(503, { code: 'server.unavailable' }))],
    ['fails inside', () => Promise.resolve(answer(500, { code: 'server.error' }))],
    ['cannot be reached', () => Promise.reject(new TypeError('Failed to fetch'))],
  ])('are kept when the app starts and the server %s', async (_, refresh) => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(refresh));

    const restored = await auth.restore();

    // The sign-in page shows, and the same person finds their drafts once signed in again.
    expect(restored).toBe(false);
    expect(useSession.getState().status).toBe('anonymous');
    expect(localStorage.getItem(noteDraft)).not.toBeNull();
    expect(localStorage.getItem(otherDraft)).not.toBeNull();
  });

  it.each([401, 403])(
    'are cleared when the app starts and the server says the session is over (%i)',
    async (status) => {
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(answer(status, { code: 'auth.session_ended' })));

      const restored = await auth.restore();

      expect(restored).toBe(false);
      expect(useSession.getState().status).toBe('anonymous');
      expect(localStorage.getItem(noteDraft)).toBeNull();
      expect(localStorage.getItem(otherDraft)).toBeNull();
      expect(localStorage.getItem('hc360:theme')).toBe('dark');
    },
  );

  it('are left alone when the session is restored', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(answer(200, { accessToken: 't2', expiresAt: null, user: asha })));

    expect(await auth.restore()).toBe(true);

    expect(useSession.getState().user?.id).toBe('u-asha');
    expect(localStorage.getItem(noteDraft)).not.toBeNull();
  });
});

const ashaKey = btoa(String.fromCharCode(...new Uint8Array(32).fill(7)));
const raviKey = btoa(String.fromCharCode(...new Uint8Array(32).fill(9)));
const typed = JSON.stringify({ value: { note: 'BP 150/95, repeat in an hour' }, savedAt: 1 });

/** What the computer holds for a draft once it is kept encrypted. */
async function sealedOnComputer(storageKey = noteDraft): Promise<string> {
  await vi.waitFor(() => expect(localStorage.getItem(storageKey)).toMatch(/^\{"v":2,/));
  return localStorage.getItem(storageKey)!;
}

/** A draft as another of the app's windows keeps it: encrypted under the user's key, bound to its storage key. */
async function sealedElsewhere(draftKey: string, storageKey: string, value: string): Promise<string> {
  const raw = Uint8Array.from(atob(draftKey), (c) => c.charCodeAt(0));
  const key = await crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['encrypt']);
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = new Uint8Array(
    await crypto.subtle.encrypt(
      { name: 'AES-GCM', iv, additionalData: new TextEncoder().encode(storageKey) },
      key,
      new TextEncoder().encode(value),
    ),
  );
  return JSON.stringify({ v: 2, iv: btoa(String.fromCharCode(...iv)), data: btoa(String.fromCharCode(...data)) });
}

/**
 * Audit P10: a draft holds clinical text, so the computer keeps it encrypted, under a key the server gives each signed-in
 * user. The same user finds it again after a crash or an outage; someone else signed in on the same Windows account
 * cannot read it.
 */
describe('drafts on this computer', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    clearDrafts();
    useSession.setState({ status: 'unknown', accessToken: null, expiresAt: null, user: null, locked: false });
    localStorage.clear();
  });

  it('hold no clinical text, only what the user’s key opens', async () => {
    await openDrafts('u-asha', ashaKey);

    draftStorage.setItem(noteDraft, typed);

    expect(draftStorage.getItem(noteDraft)).toBe(typed);
    expect(await sealedOnComputer()).not.toContain('BP 150/95');
  });

  it('open again after a crash, once the same user signs in again', async () => {
    await openDrafts('u-asha', ashaKey);
    draftStorage.setItem(noteDraft, typed);
    await sealedOnComputer();

    // HC360 Desktop crashes: what it held in memory is gone.
    closeDrafts();
    expect(draftStorage.getItem(noteDraft)).toBeNull();
    await openDrafts('u-asha', ashaKey);

    expect(draftStorage.getItem(noteDraft)).toBe(typed);
  });

  it('cannot be read by someone else signed in on the same Windows account', async () => {
    await openDrafts('u-asha', ashaKey);
    draftStorage.setItem(noteDraft, typed);
    await sealedOnComputer();
    closeDrafts();

    await openDrafts('u-ravi', raviKey);
    expect(draftStorage.getItem(noteDraft)).toBeNull();
    closeDrafts();
    // Not even by copying Asha's draft under his own name, or opening it with his key under hers.
    localStorage.setItem(otherDraft, localStorage.getItem(noteDraft)!);
    await openDrafts('u-ravi', raviKey);
    expect(draftStorage.getItem(otherDraft)).toBeNull();
    closeDrafts();
    await openDrafts('u-asha', raviKey);
    expect(draftStorage.getItem(noteDraft)).toBeNull();

    // It is still there for Asha.
    closeDrafts();
    await openDrafts('u-asha', ashaKey);
    expect(draftStorage.getItem(noteDraft)).toBe(typed);
  });

  it('cannot stand in for another form’s draft', async () => {
    const otherNote = draftStorageKey('u-asha', 'nursing-note:adm-2');
    await openDrafts('u-asha', ashaKey);
    draftStorage.setItem(noteDraft, typed);
    localStorage.setItem(otherNote, await sealedOnComputer());
    closeDrafts();

    await openDrafts('u-asha', ashaKey);

    expect(draftStorage.getItem(noteDraft)).toBe(typed);
    expect(draftStorage.getItem(otherNote)).toBeNull();
  });

  it('kept unencrypted by an older HC360 are kept again encrypted', async () => {
    localStorage.setItem(noteDraft, typed);

    await openDrafts('u-asha', ashaKey);

    expect(draftStorage.getItem(noteDraft)).toBe(typed);
    expect(await sealedOnComputer()).not.toContain('BP 150/95');
  });

  it('typed before the key comes are kept in memory, and on the computer encrypted once it does', async () => {
    draftStorage.setItem(noteDraft, typed);
    await Promise.resolve();
    expect(localStorage.getItem(noteDraft)).toBeNull();

    await openDrafts('u-asha', ashaKey);

    expect(draftStorage.getItem(noteDraft)).toBe(typed);
    expect(await sealedOnComputer()).not.toContain('BP 150/95');
  });

  it('kept on the computer are not taken away by a form that looked before they were open', async () => {
    localStorage.setItem(noteDraft, await sealedElsewhere(ashaKey, noteDraft, typed));

    // The app starts on the form after a crash, and the form finds no draft yet.
    draftStorage.removeItem(noteDraft);
    await openDrafts('u-asha', ashaKey);

    expect(draftStorage.getItem(noteDraft)).toBe(typed);
    // Once open, removing one removes it from the computer too.
    draftStorage.removeItem(noteDraft);
    expect(localStorage.getItem(noteDraft)).toBeNull();
  });

  it('are kept in memory only on a page without WebCrypto, and those on the computer stay there unread', async () => {
    localStorage.setItem(noteDraft, await sealedElsewhere(ashaKey, noteDraft, typed));
    const otherNote = draftStorageKey('u-asha', 'nursing-note:adm-2');
    vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto), subtle: undefined });

    await openDrafts('u-asha', ashaKey);
    draftStorage.removeItem(noteDraft);
    draftStorage.setItem(otherNote, typed);
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(useDrafts.getState().openFor).toBeNull();
    expect(draftStorage.getItem(otherNote)).toBe(typed);
    expect(localStorage.getItem(otherNote)).toBeNull();
    expect(localStorage.getItem(noteDraft)).not.toBeNull();
  });

  it('open with the key the server gives at sign-in, and go at sign-out', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(answer(200, { accessToken: 't2', expiresAt: null, user: asha, draftKey: ashaKey })),
    );
    expect(await auth.restore()).toBe(true);
    await vi.waitFor(() => expect(useDrafts.getState().openFor).toBe('u-asha'));
    draftStorage.setItem(noteDraft, typed);
    await sealedOnComputer();

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })));
    await auth.logout();

    expect(draftStorage.getItem(noteDraft)).toBeNull();
    expect(localStorage.getItem(noteDraft)).toBeNull();
    expect(useDrafts.getState().openFor).toBeNull();
  });

  it('stay closed while the server gives no key, as when the session is locked', async () => {
    localStorage.setItem(noteDraft, await sealedElsewhere(ashaKey, noteDraft, typed));
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValue(
          answer(200, { accessToken: 't2', expiresAt: null, user: { ...asha, restriction: 'locked' } }),
        ),
    );

    expect(await auth.restore()).toBe(true);

    expect(useDrafts.getState().openFor).toBeNull();
    expect(draftStorage.getItem(noteDraft)).toBeNull();
    expect(localStorage.getItem(noteDraft)).not.toBeNull();
  });

  it('kept or removed in another of the app’s windows are kept or removed here too', async () => {
    await openDrafts('u-asha', ashaKey);
    const sealed = await sealedElsewhere(ashaKey, noteDraft, typed);

    window.dispatchEvent(new StorageEvent('storage', { key: noteDraft, newValue: sealed }));
    await vi.waitFor(() => expect(draftStorage.getItem(noteDraft)).toBe(typed));
    window.dispatchEvent(new StorageEvent('storage', { key: noteDraft, newValue: null }));

    expect(draftStorage.getItem(noteDraft)).toBeNull();
  });
});
