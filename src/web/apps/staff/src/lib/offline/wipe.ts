import { clearOfflineCopy } from './offlineCopy';
import { forgetOfflineUnlock } from './offlineUnlock';
import { indexedDbBackend } from './vault';

/** At sign-out: wipes the offline copy from this computer, reachable or not, and what the lock screen opens it with. */
export async function wipeOfflineCopy(): Promise<void> {
  forgetOfflineUnlock();
  await clearOfflineCopy(indexedDbBackend());
}
