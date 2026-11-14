import { clearDrafts, useConnection, useSession, type AuthUser } from '@hc360/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { DowntimePack } from '@/modules/downtime/continuityApi';
import { startOfflineCopy } from './keeper';
import { useOfflineCopy } from './offlineCopy';
import { forgetOfflineUnlock, offlineSecrets, rememberOfflineSecret } from './offlineUnlock';

// The server is outside the test: what the keeper asks it for, and keeps, is what is tested.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn() } }));

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
  permissions: ['inpatient.read'],
  facilities: [],
  mfaEnabled: false,
  hasPin: true,
  mustChangePassword: false,
  restriction: null,
};

const DRAFT_KEY = btoa(String.fromCharCode(...Array.from({ length: 32 }, (_, i) => i + 1)));

const PACK = { id: 'p1', generatedAt: '2026-11-14T08:00:00Z', sections: [] } as unknown as DowntimePack;

let stop: () => void = () => undefined;

afterEach(() => {
  stop();
  vi.mocked(api.get).mockReset();
  forgetOfflineUnlock();
  clearDrafts();
  localStorage.clear();
  useOfflineCopy.setState({ userId: null, takenAt: null, pack: null, worklists: [] });
  useConnection.setState({ browserOnline: true, serverReachable: true, databaseDown: false });
  useSession.setState({ status: 'unknown', accessToken: null, expiresAt: null, user: null, locked: false });
});

/** Signs Asha in as the sign-in page does: the server's answer carries her key, before any screen of the app is up. */
function signIn(): void {
  useSession.getState().setAuth({ accessToken: 't1', expiresAt: null, user: asha, draftKey: DRAFT_KEY });
}

describe('the offline copy kept from app start', () => {
  it('is taken as soon as the key comes with the sign-in, not at the first refresh', async () => {
    vi.mocked(api.get).mockImplementation(async (path: string) => {
      if (path === '/api/v1/downtime/pack/latest') {
        return PACK;
      }
      throw new Error('404');
    });
    stop = startOfflineCopy();

    signIn();

    await vi.waitFor(() => expect(useOfflineCopy.getState().pack?.id).toBe('p1'));
    expect(useOfflineCopy.getState().userId).toBe('u-asha');
    // The lock screen's key came with it: a PIN the server accepts can be kept for the offline check.
    await rememberOfflineSecret('u-asha', 'pin', '2468');
    expect(offlineSecrets('u-asha')).toEqual(['pin']);
  });

  it('is not taken while the server is down', async () => {
    vi.mocked(api.get).mockResolvedValue(PACK);
    useConnection.setState({ serverReachable: true, databaseDown: true });
    stop = startOfflineCopy();

    signIn();
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(api.get).not.toHaveBeenCalled();
  });

  it('goes, with what opens it from the lock screen, when the session ends while the server can be reached', async () => {
    vi.mocked(api.get).mockImplementation(async (path: string) => {
      if (path === '/api/v1/downtime/pack/latest') {
        return PACK;
      }
      throw new Error('404');
    });
    stop = startOfflineCopy();
    signIn();
    await vi.waitFor(() => expect(useOfflineCopy.getState().takenAt).not.toBeNull());
    await rememberOfflineSecret('u-asha', 'pin', '2468');

    // The server says the session is over (it expired): it ends here too.
    useSession.getState().clear();

    await vi.waitFor(() => expect(useOfflineCopy.getState().takenAt).toBeNull());
    expect(localStorage.getItem('hc360:offline-unlock')).toBeNull();
  });
});
