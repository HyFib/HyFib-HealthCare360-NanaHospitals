import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { VideoUnavailable } from '@/components/FeatureOff';
import { TextLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { Notice } from '@/components/States';
import { useT } from '@/i18n';
import { usePortalFeatures } from '@/lib/features';
import { isGuid } from '@/lib/ids';
import { useCallActivity } from '@/lib/sessionEnd';
import { VideoConsultation } from '@/modules/video/VideoConsultation';
import { useHospitalName } from '@/shell/hooks';
import { PortalFrame } from '@/shell/PortalFrame';

/**
 * The join link sent by SMS/WhatsApp: /portal/telehealth/{sessionId}?t={token}. No sign-in; the hub checks the
 * token. The token is moved out of the address bar at once (into this tab's session storage), so it does not linger
 * in history, screenshots or synced browser tabs.
 */
export const Route = createFileRoute('/telehealth/$sessionId')({
  validateSearch: (search: Record<string, unknown>): { t?: string } => ({
    // Join tokens are 43 URL-safe characters (the hub refuses anything over 128).
    t: typeof search.t === 'string' && /^[A-Za-z0-9_\-.~]{16,128}$/.test(search.t) ? search.t : undefined,
  }),
  component: GuestVideoPage,
});

const tokenKey = (sessionId: string) => `hc360:portal:tele:${sessionId}`;

function readToken(sessionId: string): string | null {
  try {
    return sessionStorage.getItem(tokenKey(sessionId));
  } catch {
    return null;
  }
}

function GuestVideoPage() {
  const { sessionId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const t = useT();
  const hospital = useHospitalName();
  const [token] = useState(() => search.t ?? readToken(sessionId));
  // Turned off by the hospital: nobody joins, but a call already running is never cut off.
  const { video } = usePortalFeatures();
  const inCall = useCallActivity((s) => s.inCall);

  useEffect(() => {
    if (!search.t) {
      return;
    }
    try {
      sessionStorage.setItem(tokenKey(sessionId), search.t);
    } catch {
      // Without storage the link simply has to be opened again after a reload.
    }
    void navigate({ search: {}, replace: true });
  }, [search.t, sessionId, navigate]);

  return (
    <PortalFrame navigation={false} brandTarget="/sign-in">
      <PortalPage title={t('video.guest.title')} subtitle={t('video.guest.body', { hospital })} width="wide">
        {!video && !inCall ? (
          <VideoUnavailable />
        ) : !isGuid(sessionId) || !token ? (
          <>
            <Notice intent="warning">{t('video.guest.noToken')}</Notice>
            <TextLink to="/sign-in">{t('video.guest.signIn')}</TextLink>
          </>
        ) : (
          <VideoConsultation sessionId={sessionId} identity={{ kind: 'guest', token }} />
        )}
      </PortalPage>
    </PortalFrame>
  );
}
