import { Button, makeStyles, mergeClasses, Tab, TabList, tokens } from '@fluentui/react-components';
import { ReceiptRegular, WalletRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useInfiniteQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { CardLink, TextLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { billQueries } from '@/modules/bills/api';
import { BILL_STATUS, BILL_TYPE, isDue, totalDue } from '@/modules/bills/labels';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/bills/')({
  validateSearch: (search: Record<string, unknown>): { show?: 'due' } => ({
    show: search.show === 'due' ? 'due' : undefined,
  }),
  component: BillsPage,
});

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  due: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorStatusWarningBackground1,
    border: `1px solid ${tokens.colorStatusWarningBorder1}`,
  },
  paid: {
    backgroundColor: tokens.colorStatusSuccessBackground1,
    border: `1px solid ${tokens.colorStatusSuccessBorder1}`,
  },
  dueIcon: { fontSize: '32px', flexShrink: 0 },
  dueText: { display: 'flex', flexDirection: 'column', gap: '2px' },
  dueAmount: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  more: { alignSelf: 'center', minWidth: '200px' },
  dueLine: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorStatusWarningForeground3 },
});

function BillsPage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { show } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { profile } = useActiveProfile();
  const bills = useInfiniteQuery(billQueries.list(profile.id));
  const all = bills.data?.pages.flatMap((page) => page.items) ?? [];
  const dueBills = all.filter((bill) => isDue(bill.dueAmount));
  const rows = show === 'due' ? dueBills : all;
  const due = totalDue(all);

  return (
    <PortalPage title={t('bills.title')} subtitle={t('bills.subtitle')}>
      {bills.isSuccess && all.length > 0 && (
        <div className={mergeClasses(styles.due, due <= 0 && styles.paid)} role="status">
          <WalletRegular className={styles.dueIcon} aria-hidden />
          <span className={styles.dueText}>
            <span className={styles.dueAmount}>
              {due > 0 ? t('bills.due.total', { amount: f.inr(due) }) : t('bills.emptyDue.title')}
            </span>
            <span>{due > 0 ? t('bills.due.count', { count: dueBills.length }) : t('bills.emptyDue.body')}</span>
          </span>
        </div>
      )}

      <TabList
        selectedValue={show ?? 'all'}
        onTabSelect={(_, data) =>
          void navigate({ search: { show: data.value === 'due' ? 'due' : undefined }, replace: true })
        }
        aria-label={t('bills.filter.label')}
        size="large"
      >
        <Tab value="all">{t('bills.filter.all')}</Tab>
        <Tab value="due">{t('bills.filter.due')}</Tab>
      </TabList>

      <div className={styles.list} role="tabpanel" aria-busy={bills.isFetching}>
        {bills.isPending ? (
          <LoadingCards count={3} />
        ) : bills.isError ? (
          <ErrorBlock error={bills.error} onRetry={() => void bills.refetch()} />
        ) : rows.length === 0 ? (
          <EmptyState
            icon={<ReceiptRegular />}
            title={t(show === 'due' ? 'bills.emptyDue.title' : 'bills.empty.title')}
            description={t(show === 'due' ? 'bills.emptyDue.body' : 'bills.empty.body')}
          />
        ) : (
          <>
            {rows.map((bill) => {
              const status = BILL_STATUS[bill.status];
              return (
                <CardLink key={bill.id} to="/bills/$billId" params={{ billId: bill.id }}>
                  <ListRow
                    icon={<ReceiptRegular />}
                    tone={isDue(bill.dueAmount) ? 'warning' : 'success'}
                    title={`${t(BILL_TYPE[bill.type])}${bill.billNumber ? ` · ${bill.billNumber}` : ''}`}
                    subtitle={`${f.date(bill.billedAt)} · ${f.inr(bill.patientAmount)}`}
                    meta={
                      isDue(bill.dueAmount) ? (
                        <span className={styles.dueLine}>{t('bills.dueLine', { amount: f.inr(bill.dueAmount) })}</span>
                      ) : undefined
                    }
                    trailing={<StatusPill tone={status.tone}>{t(status.key)}</StatusPill>}
                  />
                </CardLink>
              );
            })}
            {bills.hasNextPage && (
              <Button
                className={styles.more}
                size="large"
                onClick={() => void bills.fetchNextPage()}
                disabled={bills.isFetchingNextPage}
              >
                {t('common.showMore')}
              </Button>
            )}
          </>
        )}
      </div>
      <TextLink to="/rates">{t('rates.link')}</TextLink>
    </PortalPage>
  );
}
