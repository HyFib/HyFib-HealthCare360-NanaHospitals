import { onSessionKey, serverOutage, useConnection, useSession } from '@hc360/core';
import { api } from '@/lib/api';
import { clearOfflineCopy, openOfflineCopy, takeOfflineCopy } from './offlineCopy';
import { forgetOfflineUnlock, openOfflineUnlock } from './offlineUnlock';
import { indexedDbBackend } from './vault';

/** Where the offline copy is kept on this computer (IndexedDB), or null where there is no such storage. */
export const offlineBackend = indexedDbBackend();

/** Reads what the copy holds from the server. */
export const fetchOfflineJson = (path: string) => api.get<unknown>(path);

/**
 * Keeps the offline copy from the moment the app starts, not from when its frame is up: the user's key comes with the
 * sign-in, or with the refresh that restores the session as the app starts, both before the frame is there to hear it.
 * Each time it comes, the copy's key and the lock screen's are derived from it (another user's copy and verifiers are
 * wiped first) and, while HC360 can work, the copy is taken. A session that ends while HC360 can be reached wipes them
 * (a sign-out has already: `signOut`); one that ends unseen during an outage keeps them, for the locked screen.
 */
export function startOfflineCopy(): () => void {
  const stopKeys = onSessionKey((userId, serverKey) => {
    openOfflineUnlock(userId, serverKey);
    void openOfflineCopy(userId, serverKey, offlineBackend).then(() =>
      serverOutage(useConnection.getState())
        ? false
        : takeOfflineCopy({ backend: offlineBackend, fetchJson: fetchOfflineJson }),
    );
  });
  const stopSession = useSession.subscribe((state, previous) => {
    if (
      previous.status === 'authenticated' &&
      state.status === 'anonymous' &&
      useConnection.getState().serverReachable
    ) {
      forgetOfflineUnlock();
      void clearOfflineCopy(offlineBackend);
    }
  });
  return () => {
    stopKeys();
    stopSession();
  };
}
