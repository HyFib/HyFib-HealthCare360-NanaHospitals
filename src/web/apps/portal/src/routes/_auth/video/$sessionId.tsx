import { makeStyles, tokens } from '@fluentui/react-components';
import { ClockRegular, PersonFeedbackRegular, PillRegular } from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { VideoUnavailable } from '@/components/FeatureOff';
import { BackLink, ButtonLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { formatCountdown } from '@/i18n/format';
import { useFormat } from '@/i18n/useFormat';
import { usePortalFeatures } from '@/lib/features';
import { useNow } from '@/lib/hooks';
import { isGuid } from '@/lib/ids';
import { useCallActivity } from '@/lib/sessionEnd';
import { videoQueries } from '@/modules/video/api';
import { ChatTranscript } from '@/modules/video/ChatLog';
import { isClosedSession } from '@/modules/video/labels';
import { VideoConsultation } from '@/modules/video/VideoConsultation';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/video/$sessionId')({
  component: VideoSessionPage,
});

const useStyles = makeStyles({
  wait: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '24px 16px',
    textAlign: 'center',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  waitIcon: { fontSize: '40px', color: tokens.colorBrandForeground1 },
  countdown: {
    fontSize: tokens.fontSizeHero800,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  muted: { margin: 0, color: tokens.colorNeutralForeground2 },
});

function VideoSessionPage() {
  const { sessionId } = Route.useParams();
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { profile } = useActiveProfile();
  // Turned off by the hospital: nobody joins, but a call already running is never cut off.
  const { video } = usePortalFeatures();
  const inCall = useCallActivity((s) => s.inCall);
  const videoOff = !video && !inCall;
  const info = useQuery({
    ...videoQueries.joinInfo(profile.id, sessionId),
    enabled: isGuid(sessionId) && !videoOff,
    // Poll only while waiting for the join window; once joinable, nothing may disturb the call on screen.
    refetchInterval: (query) =>
      query.state.data && !query.state.data.canJoinNow && !isClosedSession(query.state.data.status) ? 30_000 : false,
  });
  const waiting = info.data ? !info.data.canJoinNow && !isClosedSession(info.data.status) : false;
  const now = useNow(1000, waiting);
  const back = <BackLink to="/video">{t('video.title')}</BackLink>;

  if (videoOff) {
    return (
      <PortalPage title={t('video.title')} back={back} width="narrow">
        <VideoUnavailable />
      </PortalPage>
    );
  }
  if (!isGuid(sessionId)) {
    return (
      <PortalPage title={t('video.title')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }
  if (info.isPending) {
    return <PageSpinner />;
  }
  if (!info.data) {
    return (
      <PortalPage title={t('video.title')} back={back} width="narrow">
        <ErrorBlock error={info.error} onRetry={() => void info.refetch()} />
      </PortalPage>
    );
  }

  const session = info.data;
  const title = t('video.call.label', { doctor: session.practitionerName });
  const subtitle = [
    session.departmentName,
    `${f.dateLong(session.scheduledStartAt)}, ${f.time(session.scheduledStartAt)}`,
    t('common.forPerson', { name: session.patientName }),
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <PortalPage title={title} subtitle={subtitle} back={back} width="wide">
      {isClosedSession(session.status) ? (
        <>
          <Notice intent={session.status === 'Completed' ? 'success' : 'warning'}>
            {t(
              session.status === 'Cancelled'
                ? 'video.ended.cancelled'
                : session.status === 'Missed'
                  ? 'video.ended.missed'
                  : 'video.ended.body',
            )}
          </Notice>
          {session.status === 'Completed' && <ChatTranscript patientId={profile.id} sessionId={session.sessionId} />}
        </>
      ) : !session.canJoinNow ? (
        <div className={styles.wait} role="status">
          <ClockRegular className={styles.waitIcon} aria-hidden />
          <span className={styles.countdown}>
            {formatCountdown(Math.max(0, Date.parse(session.joinOpensAt) - now))}
          </span>
          <p className={styles.muted}>{t('video.opensAt', { time: f.dateTime(session.joinOpensAt) })}</p>
        </div>
      ) : (
        <VideoConsultation
          sessionId={session.sessionId}
          hubPath={session.hubPath}
          identity={{ kind: 'patient' }}
          iceServers={session.iceServers}
          endActions={
            <>
              <ButtonLink appearance="secondary" icon={<PillRegular />} to="/prescriptions">
                {t('video.ended.prescriptions')}
              </ButtonLink>
              <ButtonLink appearance="secondary" icon={<PersonFeedbackRegular />} to="/my-feedback">
                {t('video.ended.feedback')}
              </ButtonLink>
            </>
          }
        />
      )}
    </PortalPage>
  );
}
