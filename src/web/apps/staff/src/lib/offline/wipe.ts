import { clearOfflineCopy } from './offlineCopy';
import { indexedDbBackend } from './vault';

/** At sign-out: wipes the offline copy from this computer, reachable or not. */
export async function wipeOfflineCopy(): Promise<void> {
  await clearOfflineCopy(indexedDbBackend());
}
