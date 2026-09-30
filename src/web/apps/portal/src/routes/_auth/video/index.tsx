import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { VideoRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { ButtonLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { formatCountdown } from '@/i18n/format';
import { useFormat } from '@/i18n/useFormat';
import { useNow } from '@/lib/hooks';
import type { PortalTelehealthSession } from '@/lib/types';
import { describeWhen } from '@/lib/when';
import { videoQueries } from '@/modules/video/api';
import { isClosedSession, VIDEO_STATUS } from '@/modules/video/labels';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/video/')({
  component: VideoListPage,
});

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  sectionTitle: { margin: '8px 0 0', fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '14px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  ready: { border: `2px solid ${tokens.colorStatusSuccessBorder1}`, padding: '13px' },
  head: { display: 'flex', alignItems: 'center', gap: '12px' },
  opens: { margin: 0, color: tokens.colorNeutralForeground2, fontWeight: tokens.fontWeightSemibold },
});

const SOON_MS = 6 * 60 * 60_000;

function VideoListPage() {
  const styles = useStyles();
  const t = useT();
  const { profile } = useActiveProfile();
  const sessions = useQuery(videoQueries.sessions(profile.id));
  const rows = sessions.data ?? [];
  const upcoming = rows.filter((session) => !isClosedSession(session.status));
  const earlier = rows.filter((session) => isClosedSession(session.status)).reverse();

  return (
    <PortalPage
      title={t('video.title')}
      subtitle={t('video.subtitle')}
      actions={
        <ButtonLink appearance="primary" icon={<VideoRegular />} to="/doctors" search={{ video: true }}>
          {t('video.book')}
        </ButtonLink>
      }
    >
      {sessions.isPending ? (
        <LoadingCards count={2} />
      ) : sessions.isError ? (
        <ErrorBlock error={sessions.error} onRetry={() => void sessions.refetch()} />
      ) : rows.length === 0 ? (
        <EmptyState icon={<VideoRegular />} title={t('video.empty.title')} description={t('video.empty.body')} />
      ) : (
        <>
          {upcoming.length > 0 && (
            <section className={styles.list} aria-labelledby="video-upcoming">
              <h2 id="video-upcoming" className={styles.sectionTitle}>
                {t('video.upcoming')}
              </h2>
              {upcoming.map((session) => (
                <SessionCard key={session.id} session={session} />
              ))}
            </section>
          )}
          {earlier.length > 0 && (
            <section className={styles.list} aria-labelledby="video-earlier">
              <h2 id="video-earlier" className={styles.sectionTitle}>
                {t('video.earlier')}
              </h2>
              {earlier.map((session) => (
                <SessionCard key={session.id} session={session} />
              ))}
            </section>
          )}
        </>
      )}
    </PortalPage>
  );
}

function SessionCard({ session }: { session: PortalTelehealthSession }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const opensAt = Date.parse(session.joinOpensAt);
  const closed = isClosedSession(session.status);
  const now = useNow(1000, !closed && !session.canJoinNow);
  const status = VIDEO_STATUS[session.status];
  const untilOpen = opensAt - now;

  return (
    <div className={mergeClasses(styles.card, session.canJoinNow && styles.ready)}>
      <div className={styles.head}>
        <ListRow
          icon={<VideoRegular />}
          tone={session.canJoinNow ? 'success' : closed ? 'neutral' : 'brand'}
          title={session.practitionerName}
          subtitle={describeWhen(session.scheduledStartAt, f, t, now)}
          meta={
            session.appointmentNumber ? t('appointment.shortNumber', { number: session.appointmentNumber }) : undefined
          }
          trailing={<StatusPill tone={status.tone}>{t(status.key)}</StatusPill>}
          chevron={false}
        />
      </div>
      {session.canJoinNow ? (
        <ButtonLink
          appearance="primary"
          icon={<VideoRegular />}
          to="/video/$sessionId"
          params={{ sessionId: session.id }}
        >
          {t('video.join')}
        </ButtonLink>
      ) : (
        !closed && (
          <p className={styles.opens}>
            {untilOpen > 0 && untilOpen < SOON_MS
              ? t('video.opensIn', { time: formatCountdown(untilOpen) })
              : t('video.opensAt', { time: f.dateTime(session.joinOpensAt) })}
          </p>
        )
      )}
    </div>
  );
}
