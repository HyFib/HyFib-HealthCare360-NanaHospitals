import { auth, createApiClient, isDesktop, useConnection, useSession, type ClientType } from '@hc360/core';

export const clientType: ClientType = isDesktop ? 'desktop' : 'web';

/**
 * The staff app's single API client. The UI is served by the HC360 Server, so every call is same-origin; the
 * access token lives in memory and the refresh token in an HttpOnly cookie.
 */
export const api = createApiClient({
  clientType,
  getAccessToken: () => useSession.getState().accessToken,
  refreshAccessToken: () => auth.refresh(),
  onSessionExpired: () => useSession.getState().clear(),
  onReachability: (reachable) => useConnection.getState().setReachable(reachable),
});
