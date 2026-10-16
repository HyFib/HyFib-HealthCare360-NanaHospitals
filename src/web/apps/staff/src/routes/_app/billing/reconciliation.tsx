import { makeStyles, MessageBar, MessageBarBody, Tab, TabList } from '@fluentui/react-components';
import { ArrowSwapRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso } from '@hc360/core';
import { ErrorState, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import {
  DISCREPANCY_KIND_LABEL,
  DISCREPANCY_KINDS,
  gatewayQueries,
  openCount,
  type DiscrepancyKind,
  type DiscrepancyStatus,
} from '@/modules/billing/gatewayApi';
import { GatewayExceptions } from '@/modules/billing/GatewayExceptions';
import { GatewaySettlements } from '@/modules/billing/GatewaySettlements';
import { dateParam, enumParam, pageParam } from '@/modules/billing/searchParams';
import { NoAccess } from '@/modules/billing/ui';

interface ReconciliationSearch {
  view?: 'settlements';
  status?: DiscrepancyStatus;
  kind?: DiscrepancyKind;
  page?: number;
  from?: string;
  to?: string;
}

export const Route = createFileRoute('/_app/billing/reconciliation')({
  validateSearch: (s: Record<string, unknown>): ReconciliationSearch => ({
    view: s.view === 'settlements' ? 'settlements' : undefined,
    status: enumParam(s.status, ['Open', 'Resolved'] as const),
    kind: enumParam(s.kind, DISCREPANCY_KINDS),
    page: pageParam(s.page),
    from: dateParam(s.from),
    to: dateParam(s.to),
  }),
  component: ReconciliationPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
});

/**
 * Gateway reconciliation (INT-PG): what the payment gateway paid to the bank, matched to HC360's receipts and refunds,
 * and the exceptions to look at, worst first. For accountants and administrators.
 */
function ReconciliationPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canReconcile = usePermission('billing.gateway.reconcile');
  const summary = useQuery({ ...gatewayQueries.summary(), enabled: canReconcile });
  const view = search.view ?? 'exceptions';

  if (!canReconcile) {
    return (
      <Page>
        <PageHeader title="Gateway reconciliation" />
        <NoAccess what="the gateway reconciliation" />
      </Page>
    );
  }

  const open = openCount(summary.data);
  const counts = new Map((summary.data?.open ?? []).map((k) => [k.kind, k]));

  return (
    <Page>
      <PageHeader
        title="Gateway reconciliation"
        subtitle="Online payments and refunds checked against what the payment gateway paid to the bank"
        icon={<ArrowSwapRegular />}
      />
      {summary.data && !summary.data.enabled && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Reconciliation is switched off for this facility, so settlements are not read. An administrator turns it on
            in Settings (billing.gateway.reconciliation).
          </MessageBarBody>
        </MessageBar>
      )}
      {summary.error && <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />}
      {summary.data && (
        <div className={styles.kpis}>
          <KpiCard
            label="To look at"
            value={open}
            hint={open === 0 ? 'Everything matches' : 'Worst first in the list below'}
            onClick={() => void navigate({ search: {} })}
          />
          {DISCREPANCY_KINDS.filter((kind) => counts.has(kind)).map((kind) => (
            <KpiCard
              key={kind}
              label={DISCREPANCY_KIND_LABEL[kind]}
              value={counts.get(kind)!.count}
              hint={formatInr(counts.get(kind)!.amount)}
              onClick={() => void navigate({ search: { kind } })}
            />
          ))}
          <KpiCard
            label="Last settlement"
            value={summary.data.lastSettledOn ? formatDateOnly(summary.data.lastSettledOn) : 'None yet'}
            hint={
              summary.data.lastFetchedAt
                ? `Read ${formatDateTime(summary.data.lastFetchedAt)}; receipts are expected within ${summary.data.settlementDays} days`
                : `Receipts are expected within ${summary.data.settlementDays} days`
            }
            onClick={() => void navigate({ search: { view: 'settlements' } })}
          />
        </div>
      )}
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={view}
            onTabSelect={(_, d) => void navigate({ search: d.value === 'settlements' ? { view: 'settlements' } : {} })}
          >
            <Tab value="exceptions">Exceptions</Tab>
            <Tab value="settlements">Settlements</Tab>
          </TabList>
        </div>
        {view === 'exceptions' ? (
          <GatewayExceptions
            filters={{ status: search.status, kind: search.kind, page: search.page }}
            onFilters={(next) => void navigate({ search: { status: next.status, kind: next.kind, page: next.page } })}
          />
        ) : (
          <GatewaySettlements
            from={search.from ?? todayIso(-6)}
            to={search.to ?? todayIso()}
            onRange={(from, to) => void navigate({ search: (prev) => ({ ...prev, from, to }) })}
          />
        )}
      </Section>
    </Page>
  );
}
