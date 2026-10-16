import { Button, makeStyles, tokens } from '@fluentui/react-components';
import { ArrowRepeatAllRegular, PillRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { CardLink } from '@/components/links';
import { ListRow, RowCard } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { prescriptionQueries } from '@/modules/prescriptions/api';
import { REFILL_STATUS, RX_STATUS } from '@/modules/prescriptions/labels';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/prescriptions/')({
  component: PrescriptionsPage,
});

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  sectionTitle: { margin: '8px 0 0', fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  more: { alignSelf: 'center', minWidth: '200px' },
});

function PrescriptionsPage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const navigate = useNavigate();
  const { profile } = useActiveProfile();
  const list = useInfiniteQuery(prescriptionQueries.list(profile.id));
  const refills = useQuery(prescriptionQueries.refills(profile.id));
  const rows = list.data?.pages.flatMap((page) => page.items) ?? [];
  const requests = (refills.data?.requests ?? []).slice(0, 5);

  return (
    <PortalPage title={t('rx.title')} subtitle={t('rx.subtitle')}>
      {requests.length > 0 && (
        <section className={styles.list} aria-labelledby="refills-title">
          <h2 id="refills-title" className={styles.sectionTitle}>
            {t('rx.refill.history')}
          </h2>
          {requests.map((request) => {
            const status = REFILL_STATUS[request.status];
            return (
              <RowCard key={request.id}>
                <ListRow
                  icon={<ArrowRepeatAllRegular />}
                  tone={status.tone === 'success' ? 'success' : status.tone === 'danger' ? 'danger' : 'warning'}
                  title={t('rx.number', { number: request.prescriptionNumber })}
                  subtitle={[
                    t('rx.refill.requested', { date: f.date(request.requestedAt) }),
                    request.preferredPickupDate
                      ? t('rx.refill.pickupOn', { date: f.calendarDate(request.preferredPickupDate) })
                      : null,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                  meta={request.rejectReason ? t('rx.refill.reason', { reason: request.rejectReason }) : undefined}
                  trailing={<StatusPill tone={status.tone}>{t(status.key)}</StatusPill>}
                  chevron={false}
                />
              </RowCard>
            );
          })}
        </section>
      )}

      <section className={styles.list} aria-labelledby="rx-list-title" aria-busy={list.isFetching}>
        {requests.length > 0 && (
          <h2 id="rx-list-title" className={styles.sectionTitle}>
            {t('rx.medicines')}
          </h2>
        )}
        {list.isPending ? (
          <LoadingCards count={3} />
        ) : list.isError ? (
          <ErrorBlock error={list.error} onRetry={() => void list.refetch()} />
        ) : rows.length === 0 ? (
          <EmptyState
            icon={<PillRegular />}
            title={t('rx.empty.title')}
            description={t('rx.empty.body')}
            action={
              <Button appearance="primary" size="large" onClick={() => void navigate({ to: '/doctors' })}>
                {t('nav.findDoctor')}
              </Button>
            }
          />
        ) : (
          <>
            {rows.map((rx) => {
              const status = RX_STATUS[rx.status];
              const names =
                rx.medicines.slice(0, 3).join(', ') + (rx.medicines.length > 3 ? ` +${rx.medicines.length - 3}` : '');
              return (
                <CardLink key={rx.id} to="/prescriptions/$prescriptionId" params={{ prescriptionId: rx.id }}>
                  <ListRow
                    icon={<PillRegular />}
                    tone={rx.status === 'Signed' ? 'brand' : 'neutral'}
                    title={names || t('rx.medicineCount', { count: rx.medicines.length })}
                    subtitle={[rx.doctor ? t('rx.by', { doctor: rx.doctor }) : null, f.date(rx.signedAt)]
                      .filter(Boolean)
                      .join(' · ')}
                    meta={t('rx.number', { number: rx.prescriptionNumber })}
                    trailing={
                      <>
                        <StatusPill tone={status.tone}>{t(status.key)}</StatusPill>
                        {rx.status === 'Signed' && rx.refillsRemaining > 0 && (
                          <StatusPill tone="brand">{t('rx.refillsLeft', { count: rx.refillsRemaining })}</StatusPill>
                        )}
                      </>
                    }
                  />
                </CardLink>
              );
            })}
            {list.hasNextPage && (
              <Button
                className={styles.more}
                size="large"
                onClick={() => void list.fetchNextPage()}
                disabled={list.isFetchingNextPage}
              >
                {t('common.showMore')}
              </Button>
            )}
          </>
        )}
      </section>
    </PortalPage>
  );
}
