import { Button, Field, Input, makeStyles, Text, tokens } from '@fluentui/react-components';
import { DataBarVerticalRegular, PrintRegular, ReceiptRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, todayIso, useSession } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, FilterBar, KpiCard, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { usePermission } from '@/lib/permissions';
import { canteenQueries, CUSTOMER_LABEL, PAYMENT_LABEL, type AmountByKey, type DailySalesSummary } from '@/modules/canteen/api';
import { printDailyReport } from '@/modules/canteen/print';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/canteen/report')({
  validateSearch: (s: Record<string, unknown>): { date?: string } => ({ date: typeof s.date === 'string' && DATE.test(s.date) ? s.date : undefined }),
  component: CanteenReportPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
  charts: { display: 'grid', gap: '16px', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))' },
  low: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

/** Day-end canteen report (CAN-04): sales with the GST split, collections by mode, customers, categories and items. */
function CanteenReportPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const canSeeBills = usePermission(['canteen.sales.create', 'canteen.reports.read']);
  const today = todayIso();
  const date = search.date ?? today;
  const report = useQuery(canteenQueries.daily(date));
  const r = report.data;

  return (
    <Page>
      <PageHeader
        title="Canteen day report"
        icon={<DataBarVerticalRegular />}
        subtitle={formatDateOnly(date)}
        actions={
          <>
            {canSeeBills && (
              <Button icon={<ReceiptRegular />} onClick={() => void navigate({ to: '/canteen/sales', search: date === today ? {} : { date } })}>
                Bills of the day
              </Button>
            )}
            <Button appearance="primary" icon={<PrintRegular />} disabled={!r} onClick={() => r && void printDailyReport(r, facilityName).catch((error: unknown) => notify.error(error, 'Printing failed'))}>
              Print report
            </Button>
          </>
        }
      />
      <Section>
        <FilterBar>
          <Field label="Date">
            <Input type="date" value={date} max={today} onChange={(_, d) => d.value && void navigate({ search: { date: d.value === today ? undefined : d.value }, replace: true })} />
          </Field>
        </FilterBar>
      </Section>
      {report.error && !r ? (
        <ErrorState error={report.error} onRetry={() => void report.refetch()} />
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard label="Net sales" value={r ? formatInr(r.netSales) : '…'} hint={r ? `${r.bills} bills` : undefined} />
            <KpiCard label="Taxable value" value={r ? formatInr(r.taxableAmount) : '…'} />
            <KpiCard label="CGST" value={r ? formatInr(r.cgst) : '…'} />
            <KpiCard label="SGST" value={r ? formatInr(r.sgst) : '…'} />
            <KpiCard label="Round off" value={r ? formatInr(r.roundOff) : '…'} />
            <KpiCard label="Cancelled bills" value={r ? r.cancelledBills : '…'} hint="Not in the totals" />
          </div>
          {r && r.bills === 0 ? (
            <EmptyState icon={<ReceiptRegular />} title="No sales on this day" />
          ) : (
            r && (
              <>
                <div className={styles.charts}>
                  <Section title="Collections by payment mode">
                    <BreakdownChart rows={r.byPaymentMode} labels={PAYMENT_LABEL} name="payment mode" />
                  </Section>
                  <Section title="By customer">
                    <BreakdownChart rows={r.byCustomerType} labels={CUSTOMER_LABEL} name="customer" />
                  </Section>
                  <Section title="By category">
                    <BreakdownChart rows={r.byCategory} name="category" />
                  </Section>
                </div>
                <ItemsTable report={r} lowStockClass={styles.low} />
              </>
            )
          )}
        </>
      )}
    </Page>
  );
}

function BreakdownChart({ rows, labels, name }: { rows: AmountByKey[]; labels?: Readonly<Record<string, string>>; name: string }) {
  const label = (key: string) => labels?.[key] ?? key;
  const option = useMemo<ChartOption>(
    () => ({
      tooltip: { trigger: 'item', valueFormatter: (value) => formatInr(Number(value)) },
      legend: { bottom: 0, type: 'scroll' },
      series: [
        {
          type: 'pie',
          radius: ['40%', '70%'],
          avoidLabelOverlap: true,
          label: { show: false },
          data: rows.map((row) => ({ name: `${labels?.[row.key] ?? row.key} (${row.bills})`, value: row.amount })),
        },
      ],
    }),
    [rows, labels],
  );
  if (rows.length === 0) {
    return <Text>No sales.</Text>;
  }
  return <EChart option={option} height={240} ariaLabel={`Sales by ${name}: ${rows.map((row) => `${label(row.key)} ${formatInr(row.amount)}`).join(', ')}`} />;
}

function ItemsTable({ report, lowStockClass }: { report: DailySalesSummary; lowStockClass: string }) {
  return (
    <Section title="Items sold" description="With closing stock of packaged items" flush>
      <DataTable
        ariaLabel="Items sold"
        rows={report.items}
        getRowId={(i) => i.menuItemId}
        size="small"
        columns={[
          { key: 'item', header: 'Item', render: (i) => <Text weight="semibold">{i.itemName}</Text> },
          { key: 'category', header: 'Category', width: 120, priority: 'low', render: (i) => i.category ?? '—' },
          { key: 'qty', header: 'Sold', width: 80, align: 'end', render: (i) => i.quantitySold },
          { key: 'amount', header: 'Amount', width: 120, align: 'end', render: (i) => formatInr(i.amount) },
          {
            key: 'stock',
            header: 'Closing stock',
            width: 120,
            align: 'end',
            render: (i) => (i.closingStock === null ? '—' : <span className={i.closingStock <= 5 ? lowStockClass : undefined}>{i.closingStock}</span>),
          },
        ]}
        emptyTitle="No items sold"
      />
    </Section>
  );
}
