import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  checkOfflineSecret,
  forgetOfflineUnlock,
  offlineSecrets,
  openOfflineUnlock,
  rememberOfflineSecret,
  useOfflineUnlock,
} from './offlineUnlock';
import { wipeOfflineCopy } from './wipe';

// The lock screen's check of a PIN or password on this computer, for opening the offline copy during an outage: what
// is kept is a salted slow hash sealed under the session's key, never the secret; it opens for its own user only; five
// wrong tries wipe it, and so do signing out and another user's sign-in.

/** Each user's key from the server (AuthResult.draftKey), the same in each of their sessions. */
const ASHA_KEY = btoa(String.fromCharCode(...Array.from({ length: 32 }, (_, i) => i + 1)));
const RAVI_KEY = btoa(String.fromCharCode(...Array.from({ length: 32 }, (_, i) => 200 - i)));

/** What this computer holds for the lock screen, as text. */
const kept = () => localStorage.getItem('hc360:offline-unlock') ?? '';

afterEach(() => {
  vi.restoreAllMocks();
  forgetOfflineUnlock();
  localStorage.clear();
});

describe('the offline check of a PIN or password', () => {
  it('keeps no PIN or password, only a salted hash of many rounds, sealed under the session key', async () => {
    const stretch = vi.spyOn(crypto.subtle, 'deriveBits');
    openOfflineUnlock('u-asha', ASHA_KEY);

    await rememberOfflineSecret('u-asha', 'pin', '2468');
    const first = JSON.parse(kept()) as { pin: { salt: string; iterations: number; mac: string } };
    await rememberOfflineSecret('u-asha', 'pin', '2468');
    const second = JSON.parse(kept()) as typeof first;

    expect(kept()).not.toContain('2468');
    expect(stretch).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'PBKDF2', hash: 'SHA-512', iterations: 210_000 }),
      expect.anything(),
      256,
    );
    expect(first.pin.iterations).toBeGreaterThanOrEqual(100_000);
    expect(atob(first.pin.salt)).toHaveLength(16);
    // A new random salt each time it is kept: the same PIN never gives the same verifier.
    expect(second.pin.salt).not.toBe(first.pin.salt);
    expect(second.pin.mac).not.toBe(first.pin.mac);
    expect(offlineSecrets('u-asha')).toEqual(['pin']);
  });

  it('opens with the right PIN or password, and counts a wrong one', async () => {
    openOfflineUnlock('u-asha', ASHA_KEY);
    await rememberOfflineSecret('u-asha', 'pin', '2468');
    await rememberOfflineSecret('u-asha', 'password', 'Morning rounds at seven');

    expect(await checkOfflineSecret('u-asha', 'pin', '2468')).toEqual({ result: 'opened' });
    expect(await checkOfflineSecret('u-asha', 'password', 'Morning rounds at seven')).toEqual({ result: 'opened' });
    // The PIN does not stand for the password, nor the password for the PIN.
    expect(await checkOfflineSecret('u-asha', 'password', '2468')).toEqual({ result: 'wrong', triesLeft: 4 });
    expect(await checkOfflineSecret('u-asha', 'pin', '1357')).toEqual({ result: 'wrong', triesLeft: 3 });
  });

  it('is wiped after five wrong tries, PINs and passwords together, as five end the session', async () => {
    openOfflineUnlock('u-asha', ASHA_KEY);
    await rememberOfflineSecret('u-asha', 'pin', '2468');
    await rememberOfflineSecret('u-asha', 'password', 'Morning rounds at seven');

    for (const guess of ['1111', '1234', '9999', '0000']) {
      expect((await checkOfflineSecret('u-asha', 'pin', guess)).result).toBe('wrong');
    }
    expect(await checkOfflineSecret('u-asha', 'password', 'guess')).toEqual({ result: 'locked-out' });

    expect(await checkOfflineSecret('u-asha', 'pin', '2468')).toEqual({ result: 'unavailable' });
    expect(kept()).toBe('');
    expect(useOfflineUnlock.getState().secrets).toEqual([]);
  });

  it('never opens for another user, and goes when another user signs in', async () => {
    openOfflineUnlock('u-asha', ASHA_KEY);
    await rememberOfflineSecret('u-asha', 'pin', '2468');

    // Asha's PIN, typed on a screen of Ravi's: nothing of Asha's opens for him.
    expect(await checkOfflineSecret('u-ravi', 'pin', '2468')).toEqual({ result: 'unavailable' });
    expect(offlineSecrets('u-ravi')).toEqual([]);

    // Ravi signs in on the same computer: Asha's verifier goes.
    openOfflineUnlock('u-ravi', RAVI_KEY);
    expect(kept()).toBe('');
    expect(await checkOfflineSecret('u-asha', 'pin', '2468')).toEqual({ result: 'unavailable' });
  });

  it('cannot be checked away from a live session of its user', async () => {
    openOfflineUnlock('u-asha', ASHA_KEY);
    await rememberOfflineSecret('u-asha', 'pin', '2468');
    const onComputer = kept();

    // The app starts again (the key the server gave is in memory only), or the verifier is copied to another computer.
    vi.resetModules();
    const fresh = await import('./offlineUnlock');
    // False positive, in a test: the sealed verifier the module wrote (salt and HMAC of a PBKDF2 hash of a made-up PIN),
    // put back to show it cannot be checked away from a live session. No secret is stored.
    // codeql[js/clear-text-storage-of-sensitive-data]
    localStorage.setItem('hc360:offline-unlock', onComputer);
    expect(await fresh.checkOfflineSecret('u-asha', 'pin', '2468')).toEqual({ result: 'unavailable' });

    // Under another key it is not counted as wrong: it is no longer of use, and goes.
    fresh.openOfflineUnlock('u-asha', RAVI_KEY);
    expect(await fresh.checkOfflineSecret('u-asha', 'pin', '2468')).toEqual({ result: 'unavailable' });
    expect(kept()).toBe('');
  });

  it('opens again in the next session of the same user, with the key the server gives each of them', async () => {
    openOfflineUnlock('u-asha', ASHA_KEY);
    await rememberOfflineSecret('u-asha', 'pin', '2468');

    vi.resetModules();
    const nextSession = await import('./offlineUnlock');
    nextSession.openOfflineUnlock('u-asha', ASHA_KEY);

    expect(await nextSession.checkOfflineSecret('u-asha', 'pin', '2468')).toEqual({ result: 'opened' });
  });

  it('drops a verifier it cannot seal again, rather than keep one of a secret since changed', async () => {
    openOfflineUnlock('u-asha', ASHA_KEY);
    await rememberOfflineSecret('u-asha', 'password', 'Morning rounds at seven');
    await rememberOfflineSecret('u-asha', 'pin', '2468');

    // The password is changed in a session the server gives no key yet (one that must still set up two-step verification).
    vi.resetModules();
    const restricted = await import('./offlineUnlock');
    await restricted.rememberOfflineSecret('u-asha', 'password', 'Evening rounds at nine');
    restricted.openOfflineUnlock('u-asha', ASHA_KEY);

    expect(await restricted.checkOfflineSecret('u-asha', 'password', 'Morning rounds at seven')).toEqual({
      result: 'unavailable',
    });
    expect(await restricted.checkOfflineSecret('u-asha', 'pin', '2468')).toEqual({ result: 'opened' });
  });

  it('is wiped at sign-out', async () => {
    openOfflineUnlock('u-asha', ASHA_KEY);
    await rememberOfflineSecret('u-asha', 'pin', '2468');

    await wipeOfflineCopy();

    expect(kept()).toBe('');
    expect(await checkOfflineSecret('u-asha', 'pin', '2468')).toEqual({ result: 'unavailable' });
  });
});
