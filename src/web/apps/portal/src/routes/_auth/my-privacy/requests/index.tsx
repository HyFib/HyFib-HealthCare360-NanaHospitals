import { makeStyles, tokens } from '@fluentui/react-components';
import { ClipboardTaskListLtrRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { BackLink, CardLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { useNow } from '@/lib/hooks';
import type { PortalDataRequestItem } from '@/lib/types';
import { privacyQueries } from '@/modules/privacy/api';
import { REQUEST_STATUS, REQUEST_TYPE } from '@/modules/privacy/labels';
import { isOpenRequest, isReplyLate } from '@/modules/privacy/model';
import { RequestLinks } from '@/modules/privacy/RequestLinks';
import { useActiveProfile } from '@/shell/profileContext';

/** The person's privacy requests, newest first, with the date we reply by. */
export const Route = createFileRoute('/_auth/my-privacy/requests/')({
  component: PrivacyRequestsPage,
});

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  sectionTitle: {
    margin: '8px 0 0',
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
});

function PrivacyRequestsPage() {
  const styles = useStyles();
  const t = useT();
  const { profile } = useActiveProfile();
  const list = useQuery(privacyQueries.requests(profile.id));
  const now = useNow(60_000);

  return (
    <PortalPage
      title={t('myPrivacy.hub.requests')}
      subtitle={t('myPrivacy.requests.subtitle')}
      back={<BackLink to="/my-privacy">{t('nav.myPrivacy')}</BackLink>}
      width="narrow"
    >
      <section className={styles.list} aria-busy={list.isFetching}>
        {list.isPending ? (
          <LoadingCards count={3} />
        ) : list.isError ? (
          <ErrorBlock error={list.error} onRetry={() => void list.refetch()} />
        ) : list.data.length === 0 ? (
          <EmptyState
            icon={<ClipboardTaskListLtrRegular />}
            title={t('myPrivacy.requests.empty.title')}
            description={t('myPrivacy.requests.empty.body')}
          />
        ) : (
          list.data.map((item) => <RequestRow key={item.id} item={item} now={now} />)
        )}
      </section>

      {!list.isPending && (
        <section className={styles.list} aria-labelledby="privacy-new-request">
          <h2 id="privacy-new-request" className={styles.sectionTitle}>
            {t('myPrivacy.requests.new')}
          </h2>
          <RequestLinks />
        </section>
      )}
    </PortalPage>
  );
}

function RequestRow({ item, now }: { item: PortalDataRequestItem; now: number }) {
  const t = useT();
  const f = useFormat();
  const type = REQUEST_TYPE[item.type];
  const status = REQUEST_STATUS[item.status];
  const Icon = type.icon;
  const open = isOpenRequest(item.status);
  const late = isReplyLate(item, now);
  const subtitle = [
    t('myPrivacy.requests.number', { number: item.requestNumber }),
    t('myPrivacy.requests.received', { date: f.date(item.receivedAt) }),
  ].join(' · ');

  let meta: string | undefined;
  if (open) {
    meta = late
      ? t('myPrivacy.requests.late', { date: f.date(item.dueAt) })
      : t('myPrivacy.requests.replyBy', { date: f.date(item.dueAt) });
  } else if (item.closedAt) {
    meta = t('myPrivacy.requests.closedOn', { date: f.date(item.closedAt) });
  }

  return (
    <CardLink to="/my-privacy/requests/$requestId" params={{ requestId: item.id }}>
      <ListRow
        icon={<Icon />}
        tone={open ? (late ? 'warning' : 'brand') : 'neutral'}
        title={t(type.key)}
        subtitle={subtitle}
        meta={meta}
        trailing={<StatusPill tone={status.tone}>{t(status.key)}</StatusPill>}
      />
    </CardLink>
  );
}
