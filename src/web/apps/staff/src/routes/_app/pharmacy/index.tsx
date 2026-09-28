import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import {
  ArrowRepeatAllRegular,
  BedRegular,
  CalendarErrorRegular,
  CartRegular,
  ClipboardTaskListLtrRegular,
  ClockRegular,
  HourglassHalfRegular,
  MoneyRegular,
  PeopleQueueRegular,
  PillRegular,
  ReceiptMoneyRegular,
} from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { DataTable, ErrorState, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { usePermission } from '@/lib/permissions';
import { sumNumbers } from '@/modules/inventory/decimal';
import { expiryLabel, formatQty } from '@/modules/inventory/format';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { ExpiryText } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';
import { pharmacyQueries, paymentModeLabel, type PharmacyDashboard } from '@/modules/pharmacy/api';
import { usePharmacyLive } from '@/modules/pharmacy/counter';
import { PHARMACY_PERMISSIONS as P } from '@/modules/pharmacy/permissions';

export const Route = createFileRoute('/_app/pharmacy/')({
  validateSearch: (s: Record<string, unknown>): { storeId?: string } => ({ storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined }),
  component: PharmacyDashboardPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))' },
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1.4fr)',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  late: { color: tokens.colorPaletteRedForeground1 },
});

/** Pharmacist's home (HMS-02): pending work, today's takings by payment mode and expiry exposure. */
function PharmacyDashboardPage() {
  const styles = useStyles();
  const navigate = useNavigate({ from: Route.fullPath });
  const { storeId } = Route.useSearch();
  const canSell = usePermission(P.sales);
  const canDispense = usePermission([P.dispense, P.queueRead, P.ipIssue]);
  const working = useWorkingStore('pharmacy-op', { type: 'Pharmacy' });
  usePharmacyLive();
  const dashboard = useQuery(pharmacyQueries.dashboard(storeId ?? null));
  const d = dashboard.data;
  const value = (n: number | undefined) => (d ? (n ?? 0).toLocaleString('en-IN') : '…');

  return (
    <Page>
      <PageHeader
        title="Pharmacy"
        subtitle="Today's work, takings and expiring stock. Numbers refresh as prescriptions arrive."
        actions={
          <>
            {working.stores.length > 1 && (
              <StorePicker
                stores={working.stores}
                value={storeId ?? null}
                onChange={(id) => void navigate({ search: { storeId: id ?? undefined } })}
                label="Pharmacy"
                allowAll
                allLabel="All pharmacies"
              />
            )}
            {canSell && (
              <Button icon={<CartRegular />} onClick={() => void navigate({ to: '/pharmacy/sale' })}>
                Counter sale
              </Button>
            )}
            {canDispense && (
              <Button appearance="primary" icon={<PillRegular />} onClick={() => void navigate({ to: '/pharmacy/queue' })}>
                Dispensing queue
              </Button>
            )}
          </>
        }
      />

      {dashboard.error && !d ? (
        <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} />
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard label="OP prescriptions waiting" value={value(d?.pendingOutpatient)} icon={<PeopleQueueRegular />} onClick={() => void navigate({ to: '/pharmacy/queue' })} />
            <KpiCard
              label="IP prescriptions"
              value={value(d?.pendingInpatient)}
              icon={<BedRegular />}
              hint="To issue to the wards"
              onClick={() => void navigate({ to: '/pharmacy/queue', search: { ip: true } })}
            />
            <KpiCard label="Partly dispensed" value={value(d?.partiallyDispensed)} icon={<HourglassHalfRegular />} hint="Balance still to supply" />
            <KpiCard label="Ward indents" value={value(d?.pendingWardIndents)} icon={<ClipboardTaskListLtrRegular />} onClick={() => void navigate({ to: '/pharmacy/ward-indents' })} />
            <KpiCard label="Refill requests" value={value(d?.pendingRefills)} icon={<ArrowRepeatAllRegular />} onClick={() => void navigate({ to: '/pharmacy/refills' })} />
            <KpiCard
              label="Average wait"
              value={d ? (d.averageWaitMinutes === null ? '—' : `${Math.round(d.averageWaitMinutes)} min`) : '…'}
              icon={<ClockRegular />}
              hint="Prescription arrival to dispensing, today"
            />
            <KpiCard
              label="Sales today"
              value={d ? formatInr(d.salesAmountToday, { whole: true }) : '…'}
              icon={<ReceiptMoneyRegular />}
              hint={d ? `${d.salesToday} invoices${d.refundsToday > 0 ? ` · refunds ${formatInr(d.refundsToday)}` : ''}` : undefined}
              onClick={canSell ? () => void navigate({ to: '/pharmacy/sales' }) : undefined}
            />
            <KpiCard
              label="Expiring in 90 days"
              value={d ? formatInr(d.nearExpiryValueAtMrp, { whole: true }) : '…'}
              icon={<CalendarErrorRegular />}
              hint={d ? `${d.nearExpiryBatches} batches at MRP` : undefined}
            />
          </div>

          <div className={styles.grid}>
            <Section title="Takings today by payment mode" description="Refunds are not included">
              {d && d.byMode.length > 0 ? (
                <ModeChart dashboard={d} />
              ) : (
                <Text className={styles.muted}>{d ? 'No payments received yet today.' : 'Loading…'}</Text>
              )}
            </Section>
            <Section
              title="Expiring soonest"
              description="Pharmacy stock expiring in the next 90 days. Sell these first, or return them to the vendor before they expire."
              flush
            >
              <DataTable
                ariaLabel="Expiring soonest"
                rows={d?.nearExpiry}
                loading={dashboard.isLoading}
                getRowId={(b) => b.batchId}
                size="small"
                columns={[
                  { key: 'item', header: 'Medicine', render: (b) => <Text weight="semibold">{b.itemName}</Text> },
                  { key: 'batch', header: 'Batch', render: (b) => <Text font="monospace">{b.batchNumber}</Text> },
                  {
                    key: 'expiry',
                    header: 'Expiry',
                    render: (b) => (
                      <span>
                        <ExpiryText date={b.expiryDate} daysToExpiry={b.daysToExpiry} />
                        <Caption1 block className={b.daysToExpiry <= 30 ? styles.late : styles.muted}>
                          {expiryLabel(b.daysToExpiry)}
                        </Caption1>
                      </span>
                    ),
                  },
                  { key: 'qty', header: 'Qty', align: 'end', render: (b) => formatQty(b.quantity) },
                  { key: 'value', header: 'At MRP', align: 'end', render: (b) => formatInr(b.valueAtMrp) },
                  { key: 'store', header: 'Store', priority: 'low', render: (b) => b.storeName },
                ]}
                emptyTitle="Nothing expires in the next 90 days"
              />
            </Section>
          </div>
        </>
      )}
    </Page>
  );
}

function ModeChart({ dashboard }: { dashboard: PharmacyDashboard }) {
  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 90, right: 24, top: 8, bottom: 24 },
      tooltip: { trigger: 'axis', valueFormatter: (v) => formatInr(Number(v)) },
      xAxis: { type: 'value', axisLabel: { formatter: (v: number) => `₹${v.toLocaleString('en-IN')}` } },
      yAxis: { type: 'category', data: dashboard.byMode.map((m) => `${paymentModeLabel[m.mode]} (${m.count})`) },
      series: [{ type: 'bar', name: 'Received', data: dashboard.byMode.map((m) => m.amount), barMaxWidth: 28, label: { show: true, position: 'right', formatter: (p) => formatInr(Number(p.value)) } }],
    }),
    [dashboard],
  );
  return (
    <div>
      <EChart option={option} height={Math.max(140, dashboard.byMode.length * 48 + 40)} ariaLabel="Takings today by payment mode" />
      <Caption1>
        <MoneyRegular /> Total received {formatInr(sumNumbers(dashboard.byMode.map((m) => m.amount)))}
      </Caption1>
    </div>
  );
}
