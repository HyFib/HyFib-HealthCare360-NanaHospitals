import { Badge, Button, Caption1, makeStyles, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { ArrowUndoRegular, DismissCircleRegular } from '@fluentui/react-icons';
import { formatDate, formatDateOnly, formatInr } from '@hc360/core';
import { DataTable, ErrorState, KpiCard, LoadingState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { AddChargeButton, CancelChargeDialog, type CancellableCharge } from './ChargeDialogs';
import { useCanManageCharges } from './hooks';
import { ipQueries, type IpAccountDetail, type RunningBill } from './ipApi';
import { SERVICE_CATEGORY_LABEL } from './labels';
import { formatPercent } from './money';
import { Amount } from './ui';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '16px' },
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))' },
  muted: { color: tokens.colorNeutralForeground3 },
  service: { display: 'inline-flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
  tabs: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
});

/** New services can be charged until the account is closed (a discharged stay may still get late charges). */
function takesCharges(account: IpAccountDetail): boolean {
  return account.status !== 'Closed' && account.status !== 'Cancelled';
}

function DayChart({ bill }: { bill: RunningBill }) {
  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 64, right: 16, top: 16, bottom: 32 },
      tooltip: { trigger: 'axis', valueFormatter: (value) => formatInr(Number(value)) },
      xAxis: { type: 'category', data: bill.byDate.map((d) => formatDateOnly(d.date)) },
      yAxis: { type: 'value', axisLabel: { formatter: (value: number) => formatInr(value, { whole: true }) } },
      series: [{ type: 'bar', name: 'Services', data: bill.byDate.map((d) => d.net), barMaxWidth: 36 }],
    }),
    [bill.byDate],
  );
  return <EChart option={option} height={220} ariaLabel="Services charged per day" />;
}

/**
 * IP running bill (IPB-02): what the stay has cost so far, by day and by category, every line (returns shown as
 * returns). With billing.charges.manage the desk can add a charge by hand and cancel a line not yet billed.
 */
export function IpRunningBill({ account }: { account: IpAccountDetail }) {
  const styles = useStyles();
  const [view, setView] = useState<'category' | 'day' | 'lines'>('category');
  const [cancelling, setCancelling] = useState<CancellableCharge | null>(null);
  const canManageCharges = useCanManageCharges();
  const running = useQuery({ ...ipQueries.runningBill(account.id), refetchInterval: 120_000 });

  if (running.isLoading) {
    return <LoadingState label="Adding up the stay…" />;
  }
  if (running.error || !running.data) {
    return <ErrorState error={running.error} onRetry={() => void running.refetch()} />;
  }
  const bill = running.data;
  const refund = bill.estimatedBalance < 0;

  return (
    <div className={styles.root}>
      <div className={styles.kpis}>
        <KpiCard
          label="Services so far"
          value={<Amount value={bill.netTotal} />}
          hint={`${bill.lines.length} items${bill.returnsNet !== 0 ? ` · returns ${formatInr(bill.returnsNet)}` : ''}`}
        />
        <KpiCard
          label="Estimated payable by patient"
          value={<Amount value={bill.estimatedPatientPayable} />}
          hint={
            bill.insurance
              ? `${bill.insurance.payerName ?? 'Insurer'} share ${formatInr(bill.insurance.payerShare)}`
              : 'Self pay'
          }
        />
        <KpiCard
          label="Advance held"
          value={<Amount value={bill.depositBalance} />}
          hint={`Paid on bills ${formatInr(bill.paidOnBills)}`}
        />
        <KpiCard
          label={refund ? 'Estimated refund' : 'Estimated balance to pay'}
          value={
            <Amount
              value={Math.abs(bill.estimatedBalance)}
              tone={refund ? 'positive' : bill.estimatedBalance > 0 ? 'negative' : undefined}
            />
          }
          hint={bill.estimatedAmount > 0 ? `Estimate given: ${formatInr(bill.estimatedAmount)}` : undefined}
        />
      </div>
      {bill.estimatedAmount > 0 && bill.netTotal > bill.estimatedAmount && (
        <Caption1 className={styles.muted}>
          The stay has passed the estimate given at admission by {formatInr(bill.netTotal - bill.estimatedAmount)}.
          Consider asking for a further advance.
        </Caption1>
      )}
      {bill.insurance && (
        <Caption1 className={styles.muted}>
          {bill.insurance.payerName ?? 'Insurer'}:{' '}
          {bill.insurance.coverageLimit !== null
            ? `approved up to ${formatInr(bill.insurance.coverageLimit)}`
            : 'no cap'}
          {bill.insurance.coPayPercent > 0 ? ` · co-pay ${formatPercent(bill.insurance.coPayPercent)}` : ''} · estimated
          insurer share {formatInr(bill.insurance.payerShare)}, patient share {formatInr(bill.insurance.patientShare)}
        </Caption1>
      )}

      <Section flush>
        <div className={styles.tabs} style={{ padding: '0 8px' }}>
          <TabList selectedValue={view} onTabSelect={(_, d) => setView(d.value as typeof view)} size="small">
            <Tab value="category">By category</Tab>
            <Tab value="day">By day</Tab>
            <Tab value="lines">All services</Tab>
          </TabList>
          {takesCharges(account) && (
            <AddChargeButton
              appearance="subtle"
              target={{
                patientId: account.patientId,
                visitId: null,
                admissionId: account.admissionId,
                label: `Admission ${account.admissionNumber}`,
              }}
            />
          )}
        </div>
        {view === 'category' && (
          <DataTable
            ariaLabel="Running bill by category"
            rows={bill.byCategory}
            getRowId={(c) => c.category}
            size="small"
            columns={[
              { key: 'category', header: 'Category', render: (c) => SERVICE_CATEGORY_LABEL[c.category] },
              { key: 'items', header: 'Items', align: 'end', width: 80, render: (c) => c.items },
              {
                key: 'gross',
                header: 'Amount',
                align: 'end',
                priority: 'low',
                render: (c) => <Amount value={c.gross} />,
              },
              {
                key: 'discount',
                header: 'Discount',
                align: 'end',
                priority: 'low',
                render: (c) => <Amount value={c.discount} />,
              },
              { key: 'tax', header: 'GST', align: 'end', priority: 'low', render: (c) => <Amount value={c.tax} /> },
              { key: 'net', header: 'Net', align: 'end', render: (c) => <Amount value={c.net} strong /> },
            ]}
            emptyTitle="No services charged yet"
          />
        )}
        {view === 'day' && (
          <div style={{ padding: '8px 16px 16px' }}>
            {bill.byDate.length === 0 ? (
              <Text className={styles.muted}>No services charged yet.</Text>
            ) : (
              <DayChart bill={bill} />
            )}
          </div>
        )}
        {view === 'lines' && (
          <DataTable
            ariaLabel="All services of the stay"
            rows={bill.lines}
            getRowId={(l) => l.chargeId}
            size="small"
            columns={[
              { key: 'date', header: 'Date', width: 110, render: (l) => formatDate(l.serviceDate) },
              {
                key: 'service',
                header: 'Service',
                render: (l) => (
                  <span className={styles.service}>
                    <Text>{l.description}</Text>
                    {l.isReturn && (
                      <Badge size="small" appearance="tint" color="danger" icon={<ArrowUndoRegular />}>
                        Return
                      </Badge>
                    )}
                    <Caption1 className={styles.muted}>
                      {l.serviceCode}
                      {l.practitionerName ? ` · ${l.practitionerName}` : ''}
                    </Caption1>
                  </span>
                ),
              },
              { key: 'qty', header: 'Qty', align: 'end', width: 70, render: (l) => l.quantity },
              {
                key: 'rate',
                header: 'Rate',
                align: 'end',
                priority: 'low',
                render: (l) => <Amount value={l.unitPrice} />,
              },
              {
                key: 'net',
                header: 'Net',
                align: 'end',
                render: (l) => <Amount value={l.netAmount} strong tone={l.isReturn ? 'negative' : undefined} />,
              },
              {
                key: 'status',
                header: 'Billed',
                width: 110,
                priority: 'low',
                render: (l) => (l.billId ? 'On a bill' : l.status === 'Unbilled' ? 'Not yet' : l.status),
              },
            ]}
            rowActions={
              canManageCharges
                ? (l) =>
                    l.status === 'Unbilled' ? (
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<DismissCircleRegular />}
                        aria-label={`Cancel ${l.description}`}
                        title="Cancel this charge"
                        onClick={() =>
                          setCancelling({
                            id: l.chargeId,
                            description: l.description,
                            netAmount: l.netAmount,
                            onDraft: l.billId !== null,
                          })
                        }
                      />
                    ) : null
                : undefined
            }
            emptyTitle="No services charged yet"
          />
        )}
      </Section>
      <CancelChargeDialog charge={cancelling} onClose={() => setCancelling(null)} />
    </div>
  );
}
