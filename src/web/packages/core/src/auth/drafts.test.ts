// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { auth, draftStorageKey, useSession, type AuthUser } from './session';

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
