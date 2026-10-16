import { Button, makeStyles, Tab, TabList } from '@fluentui/react-components';
import { ArrowUploadRegular, PaymentRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso } from '@hc360/core';
import { ErrorState, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  ImportReportDialog,
  PosImports,
  PosPayouts,
  PosToLookAt,
  PosUnsettled,
} from '@/modules/billing/PosSettlements';
import { posSettlementQueries, toLookAtCount, toLookAtHint } from '@/modules/billing/posSettlementApi';
import { dateParam, pageParam } from '@/modules/billing/searchParams';
import { NoAccess } from '@/modules/billing/ui';

type View = 'attention' | 'unsettled' | 'payouts' | 'imports';

interface CardSettlementsSearch {
  view?: Exclude<View, 'attention'>;
  page?: number;
  from?: string;
  to?: string;
}

const VIEWS = ['unsettled', 'payouts', 'imports'] as const;

export const Route = createFileRoute('/_app/billing/card-settlements')({
  validateSearch: (s: Record<string, unknown>): CardSettlementsSearch => ({
    view: VIEWS.find((v) => v === s.view),
    page: pageParam(s.page),
    from: dateParam(s.from),
    to: dateParam(s.to),
  }),
  component: CardSettlementsPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
});

/**
 * Card and UPI reconciliation (INT-POS): the acquirer's settlement reports matched with HC360's card and UPI receipts,
 * what does not match, the receipts not settled yet, and the payouts by day. For accountants and administrators.
 */
function CardSettlementsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canReconcile = usePermission('billing.gateway.reconcile');
  const summary = useQuery({ ...posSettlementQueries.summary(), enabled: canReconcile });
  const [importing, setImporting] = useState(false);
  const view: View = search.view ?? 'attention';

  if (!canReconcile) {
    return (
      <Page>
        <PageHeader title="Card and UPI settlements" />
        <NoAccess what="the card and UPI reconciliation" />
      </Page>
    );
  }

  const toLookAt = toLookAtCount(summary.data);
  const page = search.page ?? 1;
  const setPage = (next: number) =>
    void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) });

  return (
    <Page>
      <PageHeader
        title="Card and UPI settlements"
        subtitle="Card and UPI payments checked against what the acquirer paid to the bank"
        icon={<PaymentRegular />}
        actions={
          <Button appearance="primary" icon={<ArrowUploadRegular />} onClick={() => setImporting(true)}>
            Import settlement report
          </Button>
        }
      />
      {summary.error && <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />}
      {summary.data && (
        <div className={styles.kpis}>
          <KpiCard
            label="To look at"
            value={toLookAt}
            hint={toLookAt === 0 ? 'Everything imported matches' : toLookAtHint(summary.data)}
            onClick={() => void navigate({ search: {} })}
          />
          <KpiCard
            label="Not settled yet"
            value={summary.data.unsettled}
            hint={
              summary.data.unsettled === 0
                ? `Receipts are expected within ${summary.data.settlementDays} days`
                : `${formatInr(summary.data.unsettledAmount)} in receipts older than ${summary.data.settlementDays} days`
            }
            onClick={() => void navigate({ search: { view: 'unsettled' } })}
          />
          <KpiCard
            label="Last payout"
            value={summary.data.lastSettledOn ? formatDateOnly(summary.data.lastSettledOn) : 'None yet'}
            hint={
              summary.data.lastImport
                ? `Last report: ${summary.data.lastImport.acquirer}, ${formatDateTime(summary.data.lastImport.importedAt)}`
                : 'Import the acquirer’s settlement report to start'
            }
            onClick={() => void navigate({ search: { view: 'payouts' } })}
          />
        </div>
      )}
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={view}
            // Four tabs do not fit a phone's width side by side: they wrap rather than widen the page.
            style={{ flexWrap: 'wrap' }}
            onTabSelect={(_, d) =>
              void navigate({ search: d.value === 'attention' ? {} : { view: d.value as Exclude<View, 'attention'> } })
            }
          >
            <Tab value="attention">To look at</Tab>
            <Tab value="unsettled">Not settled</Tab>
            <Tab value="payouts">Payouts</Tab>
            <Tab value="imports">Reports</Tab>
          </TabList>
        </div>
        {view === 'attention' && <PosToLookAt page={page} onPage={setPage} />}
        {view === 'unsettled' && <PosUnsettled settlementDays={summary.data?.settlementDays ?? 3} />}
        {view === 'payouts' && (
          <PosPayouts
            from={search.from ?? todayIso(-30)}
            to={search.to ?? todayIso()}
            onRange={(from, to) => void navigate({ search: (prev) => ({ ...prev, from, to }) })}
          />
        )}
        {view === 'imports' && <PosImports page={page} onPage={setPage} />}
      </Section>
      <ImportReportDialog
        open={importing}
        defaultAcquirer={summary.data?.lastImport?.acquirer}
        onClose={() => setImporting(false)}
      />
    </Page>
  );
}
