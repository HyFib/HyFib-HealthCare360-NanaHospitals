import { auth, createApiClient, useConnection, useSession, type ClientType } from '@hc360/core';
import { useLanguageStore } from '@/i18n';
import { useFeatureRefusals } from './featureSwitches';

export const clientType: ClientType = 'portal';

/**
 * The portal's single API client (same origin as the HC360 Server). The access token lives in memory only
 * (`useSession`); the refresh token is an HttpOnly cookie the page can never read. Every write carries an
 * Idempotency-Key, so a retry on a patchy mobile connection never books or pays twice.
 */
export const api = createApiClient({
  clientType,
  getAccessToken: () => useSession.getState().accessToken,
  refreshAccessToken: () => auth.refresh(),
  onSessionExpired: () => useSession.getState().clear(),
  onReachability: (reachable) => useConnection.getState().setReachable(reachable),
  // The hospital turned the portal or video consultations off while this page was open: explain, don't fail.
  onFeatureDisabled: (feature) => useFeatureRefusals.getState().refuse(feature),
  // The server writes its refusals and the PDFs in the patient's language: their record's preference, else this one.
  getLanguage: () => useLanguageStore.getState().language,
});
