import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  SearchBox,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { BinRecycleRegular, ReceiptAddRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, todayIso } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  ErrorState,
  FilterBar,
  KpiCard,
  LoadingState,
  Page,
  PageHeader,
  Section,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { usePermission } from '@/lib/permissions';
import {
  inventoryApi,
  inventoryKeys,
  inventoryQueries,
  type AnalysisRow,
  type ExpiringBatch,
  type ExpiryReport,
  type ReorderRow,
  type ValuationRow,
} from '@/modules/inventory/api';
import { dec, mul, sum, toNumber } from '@/modules/inventory/decimal';
import { expiryLabel, formatExpiry, formatPercent, formatQty } from '@/modules/inventory/format';
import { abcVedLabel, itemTypeLabel, type AbcClass, type FsnClass } from '@/modules/inventory/labels';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { ExpiryText } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';
import { purchaseApi } from '@/modules/purchase/api';

type ReportTab = 'near-expiry' | 'expired' | 'valuation' | 'reorder' | 'analysis';
const TABS: ReportTab[] = ['near-expiry', 'expired', 'valuation', 'reorder', 'analysis'];
const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface ReportSearch {
  tab?: ReportTab;
  storeId?: string;
  days?: number;
  from?: string;
  to?: string;
}

export const Route = createFileRoute('/_app/inventory/reports')({
  validateSearch: (s: Record<string, unknown>): ReportSearch => ({
    tab: TABS.includes(s.tab as ReportTab) ? (s.tab as ReportTab) : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    days: typeof s.days === 'number' && s.days >= 1 && s.days <= 730 ? s.days : undefined,
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
  }),
  component: ReportsPage,
});

const useStyles = makeStyles({
  kpis: {
    display: 'grid',
    gap: '12px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))',
    padding: '0 16px 12px',
  },
  charts: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(340px, 1fr))',
    padding: '0 16px 12px',
  },
  chartTitle: { marginBottom: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
  soon: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

/** Inventory reports: near-expiry, expired stock (write-off), valuation, reorder suggestions and ABC / VED / FSN analysis. */
function ReportsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const working = useWorkingStore('inventory');
  const tab = search.tab ?? 'near-expiry';
  const set = (patch: Partial<ReportSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });

  return (
    <Page>
      <PageHeader
        title="Inventory reports"
        subtitle="Figures are worked out from the live stock and ledger; they change as stock moves."
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={tab}
            onTabSelect={(_, d) => set({ tab: d.value === 'near-expiry' ? undefined : (d.value as ReportTab) })}
          >
            <Tab value="near-expiry">Near expiry</Tab>
            <Tab value="expired">Expired</Tab>
            <Tab value="valuation">Valuation</Tab>
            <Tab value="reorder">Reorder</Tab>
            <Tab value="analysis">ABC / VED / FSN</Tab>
          </TabList>
        </div>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <StorePicker
              stores={working.stores}
              value={search.storeId ?? null}
              onChange={(id) => set({ storeId: id ?? undefined })}
              allowAll
            />
            {tab === 'near-expiry' && (
              <Field label="Expiring within">
                <Select
                  value={String(search.days ?? 90)}
                  onChange={(_, d) => set({ days: Number(d.value) === 90 ? undefined : Number(d.value) })}
                >
                  {[30, 60, 90, 180, 365].map((d) => (
                    <option key={d} value={d}>
                      {d} days
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {tab === 'analysis' && (
              <>
                <Field label="From">
                  <Input
                    type="date"
                    value={search.from ?? ''}
                    max={search.to ?? todayIso()}
                    onChange={(_, d) => set({ from: d.value || undefined })}
                  />
                </Field>
                <Field label="To">
                  <Input
                    type="date"
                    value={search.to ?? ''}
                    max={todayIso()}
                    onChange={(_, d) => set({ to: d.value || undefined })}
                  />
                </Field>
                <Caption1 style={{ alignSelf: 'center' }}>Blank = the last 12 months</Caption1>
              </>
            )}
          </FilterBar>
        </div>
        {tab === 'near-expiry' && <NearExpiry storeId={search.storeId} days={search.days ?? 90} />}
        {tab === 'expired' && <Expired storeId={search.storeId} />}
        {tab === 'valuation' && <Valuation storeId={search.storeId} />}
        {tab === 'reorder' && <Reorder storeId={search.storeId} />}
        {tab === 'analysis' && <Analysis storeId={search.storeId} from={search.from} to={search.to} />}
      </Section>
    </Page>
  );
}

function ExpiryTable({
  report,
  loading,
  expired,
}: {
  report: ExpiryReport | undefined;
  loading: boolean;
  expired?: boolean;
}) {
  const styles = useStyles();
  return (
    <DataTable<ExpiringBatch>
      ariaLabel={expired ? 'Expired batches' : 'Batches expiring soon'}
      rows={report?.batches}
      loading={loading}
      getRowId={(b) => b.batchId}
      size="small"
      columns={[
        { key: 'item', header: 'Item', render: (b) => <Text weight="semibold">{b.itemName}</Text> },
        { key: 'batch', header: 'Batch', render: (b) => <Text font="monospace">{b.batchNumber}</Text> },
        { key: 'store', header: 'Store', priority: 'low', render: (b) => b.storeName },
        {
          key: 'expiry',
          header: 'Expiry',
          render: (b) => (
            <span>
              <ExpiryText date={b.expiryDate} daysToExpiry={b.daysToExpiry} />
              <Caption1 block className={b.daysToExpiry <= 30 ? styles.soon : styles.muted}>
                {expiryLabel(b.daysToExpiry)}
              </Caption1>
            </span>
          ),
        },
        { key: 'qty', header: 'Qty', align: 'end', render: (b) => formatQty(b.quantity) },
        { key: 'cost', header: 'At cost', align: 'end', render: (b) => formatInr(b.valueAtCost) },
        { key: 'mrp', header: 'At MRP', align: 'end', priority: 'low', render: (b) => formatInr(b.valueAtMrp) },
      ]}
      emptyTitle={expired ? 'No expired stock' : 'Nothing expires in this window'}
      emptyDescription={expired ? 'Expired batches appear here until they are written off.' : undefined}
    />
  );
}

function NearExpiry({ storeId, days }: { storeId?: string; days: number }) {
  const styles = useStyles();
  const report = useQuery(inventoryQueries.nearExpiry(storeId ?? null, days));
  const option = useMemo<ChartOption | null>(() => {
    const batches = report.data?.batches ?? [];
    if (batches.length === 0) {
      return null;
    }
    const byMonth = new Map<string, number>();
    for (const b of batches) {
      const month = b.expiryDate.slice(0, 7);
      byMonth.set(month, toNumber(dec(byMonth.get(month) ?? 0) + dec(b.valueAtMrp)));
    }
    const months = Array.from(byMonth.keys()).sort();
    return {
      grid: { left: 70, right: 16, top: 16, bottom: 30 },
      tooltip: { trigger: 'axis', valueFormatter: (v) => formatInr(Number(v)) },
      xAxis: { type: 'category', data: months.map((m) => formatExpiry(`${m}-01`)) },
      yAxis: { type: 'value', axisLabel: { formatter: (v: number) => `₹${v.toLocaleString('en-IN')}` } },
      series: [
        {
          type: 'bar',
          name: 'Value at MRP',
          data: months.map((m) => byMonth.get(m) ?? 0),
          itemStyle: { color: '#C27C0E' },
          barMaxWidth: 36,
        },
      ],
    };
  }, [report.data]);
  if (report.error) {
    return <ErrorState error={report.error} onRetry={() => void report.refetch()} />;
  }
  const r = report.data;
  return (
    <>
      <div className={styles.kpis}>
        <KpiCard
          label="Batches"
          value={r ? r.batchCount.toLocaleString('en-IN') : '…'}
          hint={`Expiring within ${days} days`}
        />
        <KpiCard label="Value at cost" value={r ? formatInr(r.valueAtCost, { whole: true }) : '…'} />
        <KpiCard
          label="Value at MRP"
          value={r ? formatInr(r.valueAtMrp, { whole: true }) : '…'}
          hint="Sell or return to the vendor before expiry"
        />
      </div>
      {option && (
        <div style={{ padding: '0 16px 12px' }}>
          <Text weight="semibold" block className={styles.chartTitle}>
            Value expiring by month
          </Text>
          <EChart option={option} height={220} ariaLabel="Value of stock expiring by month" />
        </div>
      )}
      <ExpiryTable report={r} loading={report.isLoading} />
    </>
  );
}

function Expired({ storeId }: { storeId?: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canWriteOff = usePermission([S.counts, S.adjustmentsApprove]);
  const [confirm, setConfirm] = useState(false);
  const report = useQuery(inventoryQueries.expired(storeId ?? null));
  const writeOff = useMutation({
    mutationFn: () => inventoryApi.expiredWriteOff(storeId!),
    onSuccess: (result) => {
      setConfirm(false);
      notify.success(`Write-off ${result.number} raised`, 'It changes stock once another person approves it.');
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.adjustments });
      void navigate({ to: '/inventory/adjustments', search: { id: result.id } });
    },
    onError: (error) => {
      setConfirm(false);
      notify.error(error, 'Could not raise the write-off');
    },
  });
  if (report.error) {
    return <ErrorState error={report.error} onRetry={() => void report.refetch()} />;
  }
  const r = report.data;
  return (
    <>
      <div className={styles.kpis}>
        <KpiCard
          label="Expired batches"
          value={r ? r.batchCount.toLocaleString('en-IN') : '…'}
          hint="Never sold or issued; waiting to be written off"
        />
        <KpiCard label="Value at cost" value={r ? formatInr(r.valueAtCost, { whole: true }) : '…'} />
        {canWriteOff && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6, justifyContent: 'center' }}>
            <Button
              appearance="primary"
              icon={<BinRecycleRegular />}
              disabled={!storeId || !r || r.batchCount === 0}
              onClick={() => setConfirm(true)}
            >
              Write off this store's expired stock
            </Button>
            {!storeId && <Caption1 className={styles.muted}>Choose a store first</Caption1>}
          </div>
        )}
      </div>
      <ExpiryTable report={r} loading={report.isLoading} expired />
      <ConfirmDialog
        open={confirm}
        title="Raise a write-off for all expired stock?"
        confirmLabel="Raise write-off"
        busy={writeOff.isPending}
        onConfirm={() => writeOff.mutate()}
        onCancel={() => setConfirm(false)}
      >
        {r ? `${r.batchCount} expired batches worth ${formatInr(r.valueAtCost)} at cost go on one adjustment. ` : ''}
        Stock changes only after another person approves it. Controlled drugs are also recorded in the register.
      </ConfirmDialog>
    </>
  );
}

function Valuation({ storeId }: { storeId?: string }) {
  const styles = useStyles();
  const [filter, setFilter] = useState('');
  const report = useQuery(inventoryQueries.valuation(storeId ?? null));
  const option = useMemo<ChartOption | null>(() => {
    const byType = report.data?.byType ?? [];
    return byType.length === 0
      ? null
      : {
          tooltip: { trigger: 'item', valueFormatter: (v) => formatInr(Number(v)) },
          legend: { bottom: 0, type: 'scroll' },
          series: [
            {
              type: 'pie',
              name: 'Value at cost',
              radius: ['40%', '70%'],
              data: byType.map((t) => ({ name: itemTypeLabel[t.type] ?? t.type, value: t.valueAtCost })),
              label: { formatter: '{b}: {d}%' },
            },
          ],
        };
  }, [report.data]);
  if (report.isLoading) {
    return <LoadingState />;
  }
  if (report.error || !report.data) {
    return <ErrorState error={report.error} onRetry={() => void report.refetch()} />;
  }
  const r = report.data;
  const term = filter.trim().toLowerCase();
  const rows = term
    ? r.rows.filter((row) => row.itemName.toLowerCase().includes(term) || row.itemCode.toLowerCase().includes(term))
    : r.rows;
  return (
    <>
      <div className={styles.kpis}>
        <KpiCard
          label="Stock at cost"
          value={formatInr(r.totalAtCost, { whole: true })}
          hint={`As of ${formatDateOnly(r.asOf)}`}
        />
        <KpiCard
          label="Stock at MRP"
          value={formatInr(r.totalAtMrp, { whole: true })}
          hint={`Margin potential ${formatInr(toNumber(dec(r.totalAtMrp) - dec(r.totalAtCost)), { whole: true })}`}
        />
      </div>
      {option && (
        <div className={styles.charts}>
          <div>
            <Text weight="semibold" block className={styles.chartTitle}>
              Value at cost by item type
            </Text>
            <EChart option={option} height={260} ariaLabel="Stock value by item type" />
          </div>
        </div>
      )}
      <div style={{ padding: '0 16px 12px', maxWidth: 360 }}>
        <SearchBox
          value={filter}
          onChange={(_, d) => setFilter(d.value)}
          placeholder="Filter items"
          aria-label="Filter items"
        />
      </div>
      <DataTable<ValuationRow>
        ariaLabel="Stock valuation"
        rows={rows}
        getRowId={(row) => `${row.storeId}-${row.itemId}`}
        size="small"
        columns={[
          { key: 'item', header: 'Item', render: (row) => <Text weight="semibold">{row.itemName}</Text> },
          { key: 'type', header: 'Type', priority: 'low', render: (row) => itemTypeLabel[row.type] },
          { key: 'store', header: 'Store', priority: 'low', render: (row) => row.storeName },
          { key: 'qty', header: 'Qty', align: 'end', render: (row) => formatQty(row.quantity) },
          { key: 'cost', header: 'At cost', align: 'end', render: (row) => formatInr(row.valueAtCost) },
          { key: 'mrp', header: 'At MRP', align: 'end', render: (row) => formatInr(row.valueAtMrp) },
        ]}
        emptyTitle="No stock to value"
      />
    </>
  );
}

function Reorder({ storeId }: { storeId?: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canRequisition = usePermission(S.purchaseManage);
  const [confirm, setConfirm] = useState(false);
  const report = useQuery(inventoryQueries.reorder(storeId ?? null));
  const rows = report.data ?? [];
  const estimate = toNumber(
    sum(rows.map((r) => (r.lastUnitCost === null ? 0n : mul(dec(r.suggestedQuantity), dec(r.lastUnitCost))))),
  );
  const requisition = useMutation({
    mutationFn: () => purchaseApi.requisitionFromReorder(storeId!),
    onSuccess: (result) => {
      setConfirm(false);
      notify.success(`Requisition ${result.number} created`, 'Review the quantities, then send it for approval.');
      void queryClient.invalidateQueries({ queryKey: ['purchase'] });
      void navigate({ to: '/purchase/requisitions/$requisitionId', params: { requisitionId: result.id } });
    },
    onError: (error) => {
      setConfirm(false);
      notify.error(error, 'Could not create the requisition');
    },
  });
  if (report.error) {
    return <ErrorState error={report.error} onRetry={() => void report.refetch()} />;
  }
  return (
    <>
      <div className={styles.kpis}>
        <KpiCard
          label="Items to reorder"
          value={report.data ? rows.length.toLocaleString('en-IN') : '…'}
          hint="Available plus on order at or below the reorder level"
        />
        <KpiCard
          label="Estimated purchase value"
          value={report.data ? formatInr(estimate, { whole: true }) : '…'}
          hint="At the last unit cost"
        />
        {canRequisition && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6, justifyContent: 'center' }}>
            <Button
              appearance="primary"
              icon={<ReceiptAddRegular />}
              disabled={!storeId || rows.length === 0}
              onClick={() => setConfirm(true)}
            >
              Create purchase requisition
            </Button>
            {!storeId && <Caption1 className={styles.muted}>Choose the store to order for</Caption1>}
          </div>
        )}
      </div>
      <DataTable<ReorderRow>
        ariaLabel="Reorder suggestions"
        rows={report.data}
        loading={report.isLoading}
        getRowId={(r) => r.itemId}
        size="small"
        columns={[
          {
            key: 'item',
            header: 'Item',
            render: (r) => (
              <span>
                <Text weight="semibold">{r.itemName}</Text>{' '}
                {r.vedClass === 'Vital' && (
                  <Badge size="small" color="danger">
                    Vital
                  </Badge>
                )}
                <Caption1 block className={styles.muted}>
                  {r.itemCode} · {itemTypeLabel[r.type]}
                </Caption1>
              </span>
            ),
          },
          { key: 'available', header: 'Available', align: 'end', render: (r) => formatQty(r.available, r.baseUnit) },
          { key: 'onOrder', header: 'On order', align: 'end', render: (r) => (r.onOrder ? formatQty(r.onOrder) : '—') },
          {
            key: 'level',
            header: 'Reorder at',
            align: 'end',
            priority: 'low',
            render: (r) => formatQty(r.reorderLevel),
          },
          {
            key: 'suggested',
            header: 'Suggested',
            align: 'end',
            render: (r) => (
              <span>
                <Text weight="semibold">{formatQty(r.suggestedPurchaseUnits, r.purchaseUnit)}</Text>
                {r.purchaseUnit !== r.baseUnit && (
                  <Caption1 block>{formatQty(r.suggestedQuantity, r.baseUnit)}</Caption1>
                )}
              </span>
            ),
          },
          {
            key: 'cost',
            header: 'Last cost',
            align: 'end',
            priority: 'low',
            render: (r) => (r.lastUnitCost === null ? '—' : formatInr(r.lastUnitCost)),
          },
        ]}
        emptyTitle="Nothing needs reordering"
        emptyDescription="Items appear here when available plus on-order stock falls to the reorder level set in the item master."
      />
      <ConfirmDialog
        open={confirm}
        title="Create a purchase requisition from this list?"
        confirmLabel="Create requisition"
        busy={requisition.isPending}
        onConfirm={() => requisition.mutate()}
        onCancel={() => setConfirm(false)}
      >
        Every suggested item (up to 300) goes on one requisition for the chosen store, in purchase units. The approver
        can still reject it.
      </ConfirmDialog>
    </>
  );
}

const ABC_COLOURS: Record<string, string> = { A: '#C50F1F', B: '#C27C0E', C: '#0A7CA7' };
const FSN_LABEL: Record<FsnClass, string> = { Fast: 'Fast moving', Slow: 'Slow moving', NonMoving: 'Not moving' };

function Analysis({ storeId, from, to }: { storeId?: string; from?: string; to?: string }) {
  const styles = useStyles();
  const [abc, setAbc] = useState<AbcClass | ''>('');
  const report = useQuery(inventoryQueries.analysis(from, to, storeId ?? null));
  const charts = useMemo(() => {
    const d = report.data;
    if (!d) {
      return null;
    }
    const abcOption: ChartOption = {
      tooltip: { trigger: 'item', valueFormatter: (v) => formatInr(Number(v)) },
      legend: { bottom: 0 },
      series: [
        {
          type: 'pie',
          name: 'Consumption value',
          radius: ['40%', '70%'],
          data: d.abc.map((c) => ({
            name: `Class ${c.class} (${c.items} items)`,
            value: c.value,
            itemStyle: { color: ABC_COLOURS[c.class] },
          })),
          label: { formatter: '{d}%' },
        },
      ],
    };
    const fsnOption: ChartOption = {
      grid: { left: 110, right: 24, top: 8, bottom: 24 },
      tooltip: { trigger: 'axis' },
      xAxis: { type: 'value', name: 'Items' },
      yAxis: { type: 'category', data: d.fsn.map((c) => FSN_LABEL[c.class as FsnClass] ?? c.class) },
      series: [{ type: 'bar', name: 'Items', data: d.fsn.map((c) => c.items), barMaxWidth: 26 }],
    };
    const matrixOption: ChartOption = {
      grid: { left: 110, right: 24, top: 8, bottom: 24 },
      tooltip: { trigger: 'axis' },
      xAxis: { type: 'value', name: 'Items' },
      yAxis: { type: 'category', data: d.abcVed.map((c) => `Category ${c.class}`) },
      series: [{ type: 'bar', name: 'Items', data: d.abcVed.map((c) => c.items), barMaxWidth: 26 }],
    };
    return { abcOption, fsnOption, matrixOption };
  }, [report.data]);

  if (report.isLoading) {
    return <LoadingState label="Working out the analysis…" />;
  }
  if (report.error || !report.data || !charts) {
    return <ErrorState error={report.error} onRetry={() => void report.refetch()} />;
  }
  const d = report.data;
  const rows = abc ? d.items.filter((i) => i.abc === abc) : d.items;
  return (
    <>
      <div className={styles.kpis}>
        <KpiCard
          label="Consumption value"
          value={formatInr(d.totalValue, { whole: true })}
          hint={`${formatDateOnly(d.from)} to ${formatDateOnly(d.to)}`}
        />
        {d.abc.map((c) => (
          <KpiCard
            key={c.class}
            label={`Class ${c.class}`}
            value={`${c.items} items`}
            hint={`${formatPercent(c.valuePercent)} of value · ${formatInr(c.value, { whole: true })}`}
          />
        ))}
      </div>
      <div className={styles.charts}>
        <div>
          <Text weight="semibold" block className={styles.chartTitle}>
            ABC: share of consumption value
          </Text>
          <Caption1 block className={styles.muted}>
            A items take about 70% of the value: control them tightly.
          </Caption1>
          <EChart option={charts.abcOption} height={240} ariaLabel="ABC classes by consumption value" />
        </div>
        <div>
          <Text weight="semibold" block className={styles.chartTitle}>
            FSN: how often items are issued
          </Text>
          <Caption1 block className={styles.muted}>
            Non-moving items tie up money and risk expiry.
          </Caption1>
          <EChart option={charts.fsnOption} height={240} ariaLabel="Fast, slow and non-moving items" />
        </div>
        <div>
          <Text weight="semibold" block className={styles.chartTitle}>
            ABC-VED control categories
          </Text>
          <Caption1 block className={styles.muted}>
            {abcVedLabel.I}
          </Caption1>
          <EChart option={charts.matrixOption} height={240} ariaLabel="ABC-VED categories" />
        </div>
      </div>
      <div style={{ padding: '0 16px 12px' }}>
        <Field label="Show class">
          <Select value={abc} onChange={(_, dd) => setAbc(dd.value as AbcClass | '')} style={{ maxWidth: 200 }}>
            <option value="">All classes</option>
            <option value="A">Class A</option>
            <option value="B">Class B</option>
            <option value="C">Class C</option>
          </Select>
        </Field>
      </div>
      <DataTable<AnalysisRow>
        ariaLabel="Item analysis"
        rows={rows}
        getRowId={(i) => i.itemId}
        size="small"
        columns={[
          { key: 'item', header: 'Item', render: (i) => <Text weight="semibold">{i.itemName}</Text> },
          { key: 'value', header: 'Consumed value', align: 'end', render: (i) => formatInr(i.consumedValue) },
          {
            key: 'share',
            header: 'Share',
            align: 'end',
            priority: 'low',
            render: (i) => formatPercent(i.sharePercent),
          },
          {
            key: 'cum',
            header: 'Cumulative',
            align: 'end',
            priority: 'low',
            render: (i) => formatPercent(i.cumulativePercent),
          },
          { key: 'issues', header: 'Issues', align: 'end', priority: 'low', render: (i) => i.issueCount },
          {
            key: 'classes',
            header: 'ABC · VED · FSN',
            render: (i) => (
              <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
                <Badge size="small" color={i.abc === 'A' ? 'danger' : i.abc === 'B' ? 'warning' : 'brand'}>
                  {i.abc}
                </Badge>
                <Badge size="small" appearance="tint">
                  {i.ved}
                </Badge>
                <Badge size="small" appearance="outline">
                  {FSN_LABEL[i.fsn]}
                </Badge>
              </span>
            ),
          },
          { key: 'cat', header: 'Control', render: (i) => `Category ${i.abcVedCategory}` },
        ]}
        emptyTitle="No consumption in this period"
      />
    </>
  );
}
