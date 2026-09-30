import { Button, makeStyles } from '@fluentui/react-components';
import {
  ArrowSwapRegular,
  BoxRegular,
  CalendarErrorRegular,
  ClipboardTaskListLtrRegular,
  DataTrendingRegular,
  ErrorCircleRegular,
  MoneyRegular,
  ScalesRegular,
  VehicleTruckProfileRegular,
} from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { ErrorState, KpiCard, Page, PageHeader } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { inventoryQueries } from '@/modules/inventory/api';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { useWorkingStore } from '@/modules/inventory/stores';
import { NearExpiryStockWidget } from '@/modules/inventory/widgets';

export const Route = createFileRoute('/_app/inventory/')({
  validateSearch: (s: Record<string, unknown>): { storeId?: string } => ({
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
  }),
  component: InventoryOverviewPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(420px, 1fr))',
    alignItems: 'start',
  },
});

/** Store manager's dashboard (HMS-02): money in stock, expiry exposure and the work waiting. */
function InventoryOverviewPage() {
  const styles = useStyles();
  const navigate = useNavigate({ from: Route.fullPath });
  const { storeId } = Route.useSearch();
  const working = useWorkingStore('inventory');
  const canRaise = usePermission(S.indentsRaise);
  const canTransfer = usePermission(S.transfers);
  const summary = useQuery(inventoryQueries.summary(storeId ?? null));
  const s = summary.data;
  const count = (n: number | undefined) => (s ? (n ?? 0).toLocaleString('en-IN') : '…');
  const storeSearch = storeId ? { storeId } : {};

  return (
    <Page>
      <PageHeader
        title="Stores and inventory"
        subtitle={storeId ? working.stores.find((x) => x.id === storeId)?.name : 'All stores of this hospital'}
        actions={
          <>
            <StorePicker
              stores={working.stores}
              value={storeId ?? null}
              onChange={(id) => void navigate({ search: { storeId: id ?? undefined } })}
              allowAll
              label="Store"
            />
            {canTransfer && (
              <Button
                icon={<ArrowSwapRegular />}
                onClick={() => void navigate({ to: '/inventory/transfers', search: { send: true } })}
              >
                Send stock
              </Button>
            )}
            {canRaise && (
              <Button
                appearance="primary"
                icon={<ClipboardTaskListLtrRegular />}
                onClick={() => void navigate({ to: '/inventory/indents', search: { raise: true } })}
              >
                Raise indent
              </Button>
            )}
          </>
        }
      />
      {summary.error && !s ? (
        <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />
      ) : (
        <div className={styles.kpis}>
          <KpiCard
            label="Stock value at cost"
            value={s ? formatInr(s.stockValueAtCost, { whole: true }) : '…'}
            icon={<MoneyRegular />}
            hint={s ? `${formatInr(s.stockValueAtMrp, { whole: true })} at MRP` : undefined}
            onClick={() => void navigate({ to: '/inventory/reports', search: { tab: 'valuation', ...storeSearch } })}
          />
          <KpiCard
            label="Expiring in 90 days"
            value={s ? formatInr(s.nearExpiryValue, { whole: true }) : '…'}
            icon={<CalendarErrorRegular />}
            hint={s ? `${s.nearExpiryBatches} batches` : undefined}
            onClick={() => void navigate({ to: '/inventory/reports', search: { tab: 'near-expiry', ...storeSearch } })}
          />
          <KpiCard
            label="Expired, not written off"
            value={s ? formatInr(s.expiredValue, { whole: true }) : '…'}
            icon={<ErrorCircleRegular />}
            hint={s ? `${s.expiredBatches} batches` : undefined}
            onClick={() => void navigate({ to: '/inventory/reports', search: { tab: 'expired', ...storeSearch } })}
          />
          <KpiCard
            label="Items to reorder"
            value={count(s?.itemsBelowReorder)}
            icon={<DataTrendingRegular />}
            hint="At or below the reorder level"
            onClick={() => void navigate({ to: '/inventory/reports', search: { tab: 'reorder', ...storeSearch } })}
          />
          <KpiCard
            label="Indents waiting"
            value={count(s?.pendingIndents)}
            icon={<BoxRegular />}
            onClick={() => void navigate({ to: '/inventory/indents' })}
          />
          <KpiCard
            label="Adjustments to approve"
            value={count(s?.pendingAdjustments)}
            icon={<ScalesRegular />}
            onClick={() => void navigate({ to: '/inventory/adjustments' })}
          />
          <KpiCard
            label="Transfers in transit"
            value={count(s?.transfersInTransit)}
            icon={<ArrowSwapRegular />}
            onClick={() => void navigate({ to: '/inventory/transfers', search: { status: 'InTransit' } })}
          />
          <KpiCard
            label="Open purchase orders"
            value={count(s?.openPurchaseOrders)}
            icon={<VehicleTruckProfileRegular />}
            onClick={() => void navigate({ to: '/purchase/orders' })}
          />
        </div>
      )}
      <div className={styles.grid}>
        <NearExpiryStockWidget />
      </div>
    </Page>
  );
}
