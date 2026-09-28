import { auth, useSession } from '@hc360/core';
import type { QueryClient } from '@tanstack/react-query';
import { useProfileStore } from './profileStore';

export type PortalSessionState = 'patient' | 'other' | 'anonymous';

/** Session storage keys the portal writes (cleared at sign-out; none of them holds patient data). */
export const SESSION_KEYS = {
  pendingPayment: 'hc360:portal:pending-payment',
} as const;

/**
 * Restores the session from the portal's HttpOnly refresh cookie (once per page load) and says whether it belongs to
 * a patient-portal user. The staff app keeps its own cookie, so a staff session in the same browser is neither seen
 * nor replaced; any session that is not a patient's is "other", and the portal never shows or uses it.
 */
export async function ensurePortalSession(): Promise<PortalSessionState> {
  const { status } = useSession.getState();
  const signedIn = status === 'authenticated' || (status === 'unknown' && (await auth.restore()));
  if (!signedIn) {
    return 'anonymous';
  }
  return useSession.getState().user?.type === 'patient' ? 'patient' : 'other';
}

/** The family list is shared by everyone on the number, so it survives a profile switch; nothing else does. */
export const PROFILES_QUERY_KEY = ['portal', 'profiles'] as const;
export const SYSTEM_INFO_QUERY_KEY = ['system-info'] as const;

/**
 * Shows another family member's records. Every cached answer of the previous person is dropped first, so no
 * screen can briefly show one person's data under another person's name.
 */
export async function switchProfile(queryClient: QueryClient, profileId: string): Promise<void> {
  const profiles = queryClient.getQueryData(PROFILES_QUERY_KEY);
  const system = queryClient.getQueryData(SYSTEM_INFO_QUERY_KEY);
  await queryClient.cancelQueries();
  queryClient.clear();
  if (profiles !== undefined) {
    queryClient.setQueryData(PROFILES_QUERY_KEY, profiles);
  }
  if (system !== undefined) {
    queryClient.setQueryData(SYSTEM_INFO_QUERY_KEY, system);
  }
  useProfileStore.getState().select(profileId);
}

/** Ends the session on the server (refresh cookie revoked) and forgets everything this tab knew. */
export async function signOut(queryClient: QueryClient): Promise<void> {
  await queryClient.cancelQueries();
  await auth.logout();
  forgetLocalState(queryClient);
}

/** Local clean-up when the session ended elsewhere (expired, revoked) or at sign-out. */
export function forgetLocalState(queryClient: QueryClient): void {
  queryClient.clear();
  useProfileStore.getState().select(null);
  try {
    Object.values(SESSION_KEYS).forEach((key) => sessionStorage.removeItem(key));
  } catch {
    // Storage unavailable: nothing was stored.
  }
}
