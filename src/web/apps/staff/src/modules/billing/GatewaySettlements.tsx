import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CloudArrowDownRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, LoadingState, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
  gatewayApi,
  gatewayKeys,
  gatewayQueries,
  SETTLEMENT_MATCH_LABEL,
  SETTLEMENT_STATUS_LABEL,
  TRANSACTION_TYPE_LABEL,
  type GatewaySettlementDto,
  type SettlementDayDto,
  type SettlementLineDto,
} from './gatewayApi';
import { ActionError, Amount } from './ui';

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  stack: { display: 'flex', flexDirection: 'column', gap: '2px' },
  dialogBody: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/**
 * The gateway's settlements by day: what reached the bank, the payments and refunds in it, the gateway's charges and
 * GST, and how many transactions do not match HC360. Choose a day to see its settlements, and a settlement to see its
 * transactions. Settlements are read every hour; "Fetch from the gateway" reads a day now.
 */
export function GatewaySettlements({
  from,
  to,
  onRange,
}: {
  from: string;
  to: string;
  onRange: (from: string, to: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [day, setDay] = useState<string | null>(null);
  const [fetchDate, setFetchDate] = useState(todayIso());
  const [open, setOpen] = useState<GatewaySettlementDto | null>(null);
  const days = useQuery(gatewayQueries.settlements(from, to));

  const fetch = useMutation({
    mutationFn: (date: string) => gatewayApi.fetch(date),
    onSuccess: (result) => {
      notify.success(`Settlements of ${formatDateOnly(result.date)}`, result.message);
      void queryClient.invalidateQueries({ queryKey: gatewayKeys.all });
    },
  });

  const selected = days.data?.find((d) => d.date === day) ?? null;

  return (
    <>
      <div style={{ padding: '8px 16px 12px' }}>
        <FilterBar>
          <Field label="From">
            <Input type="date" value={from} max={to} onChange={(_, d) => d.value && onRange(d.value, to)} />
          </Field>
          <Field label="To">
            <Input type="date" value={to} max={todayIso()} onChange={(_, d) => d.value && onRange(from, d.value)} />
          </Field>
          <Field label="Read the settlements of">
            <Input type="date" value={fetchDate} max={todayIso()} onChange={(_, d) => setFetchDate(d.value)} />
          </Field>
          <Button
            icon={fetch.isPending ? <Spinner size="tiny" /> : <CloudArrowDownRegular />}
            disabled={!fetchDate || fetch.isPending}
            onClick={() => fetch.mutate(fetchDate)}
          >
            Fetch from the gateway
          </Button>
        </FilterBar>
        <ActionError error={fetch.error} title="Could not read the settlements" />
      </div>
      <DataTable
        ariaLabel="Settlements by day"
        rows={days.data}
        loading={days.isLoading}
        error={days.error}
        onRetry={() => void days.refetch()}
        getRowId={(d) => d.date}
        onRowClick={(d) => setDay(d.date === day ? null : d.date)}
        columns={[
          {
            key: 'day',
            header: 'Day',
            width: 130,
            render: (d) => <Text weight="semibold">{formatDateOnly(d.date)}</Text>,
          },
          { key: 'count', header: 'Settlements', align: 'end', width: 110, render: (d) => d.count },
          { key: 'payments', header: 'Payments', align: 'end', render: (d) => <Amount value={d.paymentsAmount} /> },
          {
            key: 'refunds',
            header: 'Refunds',
            align: 'end',
            priority: 'low',
            render: (d) => <Amount value={d.refundsAmount} />,
          },
          {
            key: 'charges',
            header: 'Charges',
            align: 'end',
            priority: 'low',
            render: (d) => <Amount value={d.charges} />,
          },
          { key: 'gst', header: 'GST', align: 'end', priority: 'low', render: (d) => <Amount value={d.tax} /> },
          { key: 'bank', header: 'Paid to the bank', align: 'end', render: (d) => <Amount value={d.amount} strong /> },
          {
            key: 'unmatched',
            header: 'Not matched',
            align: 'end',
            width: 120,
            render: (d) => <UnmatchedBadge count={d.unmatched} />,
          },
        ]}
        emptyTitle="No settlements in this period"
        emptyDescription="The gateway's settlements are read every hour. Fetch a day to read it now."
        size="small"
      />
      {selected && <DaySettlements day={selected} onOpen={setOpen} />}
      {open && <SettlementDialog settlement={open} onClose={() => setOpen(null)} />}
      <Caption1 block className={styles.muted} style={{ padding: '8px 16px 12px' }}>
        Charges are the gateway&apos;s fees without GST. Each processed settlement is a journal in the Tally day book of
        its day: the bank for what was paid, charges and input GST, and the gross out of Payment Gateway Clearing.
      </Caption1>
    </>
  );
}

function UnmatchedBadge({ count }: { count: number }) {
  return count === 0 ? (
    <StatusBadge status="Matched" tone="success" label="All matched" />
  ) : (
    <StatusBadge status="Unmatched" tone="warning" label={`${count} to look at`} />
  );
}

function DaySettlements({
  day,
  onOpen,
}: {
  day: SettlementDayDto;
  onOpen: (settlement: GatewaySettlementDto) => void;
}) {
  const styles = useStyles();
  return (
    <Section title={`Settlements of ${formatDateOnly(day.date)}`} flush>
      <DataTable
        ariaLabel={`Settlements of ${formatDateOnly(day.date)}`}
        rows={day.settlements}
        getRowId={(s) => s.id}
        onRowClick={onOpen}
        columns={[
          {
            key: 'settlement',
            header: 'Settlement',
            render: (s) => (
              <span className={styles.stack}>
                <Text weight="semibold">{s.settlementId}</Text>
                <Caption1 className={styles.muted}>
                  {s.utr ? `UTR ${s.utr}` : 'No UTR yet'} · {formatDateTime(s.settledAt)}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'status',
            header: 'Status',
            width: 190,
            render: (s) => (
              <StatusBadge
                status={s.status}
                tone={s.status === 'Processed' ? 'success' : s.status === 'Failed' ? 'danger' : 'warning'}
                label={SETTLEMENT_STATUS_LABEL[s.status]}
              />
            ),
          },
          { key: 'payments', header: 'Payments', align: 'end', render: (s) => <Amount value={s.paymentsAmount} /> },
          {
            key: 'refunds',
            header: 'Refunds',
            align: 'end',
            priority: 'low',
            render: (s) => <Amount value={s.refundsAmount} />,
          },
          {
            key: 'fees',
            header: 'Charges + GST',
            align: 'end',
            priority: 'low',
            render: (s) => <Amount value={s.charges + s.tax} />,
          },
          { key: 'bank', header: 'Paid to the bank', align: 'end', render: (s) => <Amount value={s.amount} strong /> },
          {
            key: 'check',
            header: 'Check',
            align: 'end',
            width: 150,
            render: (s) =>
              s.difference !== 0 ? (
                <StatusBadge
                  status="Difference"
                  tone="danger"
                  label={`Differs by ${Math.abs(s.difference).toFixed(2)}`}
                />
              ) : (
                <UnmatchedBadge count={s.unmatched} />
              ),
          },
        ]}
        size="small"
      />
    </Section>
  );
}

function SettlementDialog({ settlement, onClose }: { settlement: GatewaySettlementDto; onClose: () => void }) {
  const styles = useStyles();
  const detail = useQuery(gatewayQueries.settlement(settlement.id));
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 980 }}>
        <DialogBody>
          <DialogTitle>Settlement {settlement.settlementId}</DialogTitle>
          <DialogContent className={styles.dialogBody}>
            <Text>
              <Amount value={settlement.amount} strong /> paid to the bank on {formatDateTime(settlement.settledAt)}
              {settlement.utr ? `, UTR ${settlement.utr}` : ''}.
            </Text>
            {detail.isLoading ? (
              <LoadingState label="Loading the transactions…" />
            ) : detail.error ? (
              <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />
            ) : (
              <SettlementLines lines={detail.data?.lines ?? []} />
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>
              Close
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** A settlement's transactions: what each matched in HC360 and what the gateway kept. */
export function SettlementLines({ lines }: { lines: SettlementLineDto[] }) {
  const styles = useStyles();
  return (
    <DataTable
      ariaLabel="Transactions in the settlement"
      rows={lines}
      getRowId={(l) => l.id}
      columns={[
        {
          key: 'what',
          header: 'Transaction',
          render: (l) => (
            <span className={styles.stack}>
              <Text>
                {TRANSACTION_TYPE_LABEL[l.type]} {l.entityId}
              </Text>
              <Caption1 className={styles.muted}>
                {[l.method, l.transactionAt && formatDateTime(l.transactionAt), l.paymentId && `on ${l.paymentId}`]
                  .filter(Boolean)
                  .join(' · ')}
              </Caption1>
            </span>
          ),
        },
        { key: 'amount', header: 'Amount', align: 'end', render: (l) => <Amount value={l.amount} /> },
        { key: 'fee', header: 'Fee with GST', align: 'end', priority: 'low', render: (l) => <Amount value={l.fee} /> },
        { key: 'net', header: 'To the bank', align: 'end', render: (l) => <Amount value={l.net} /> },
        {
          key: 'match',
          header: 'In HC360',
          render: (l) => (
            <span className={styles.stack}>
              <StatusBadge
                status={l.match}
                tone={l.match === 'Matched' ? 'success' : l.match === 'NotApplicable' ? 'informative' : 'danger'}
                label={SETTLEMENT_MATCH_LABEL[l.match]}
              />
              {(l.receiptNumber || l.refundNumber) && (
                <Caption1 className={styles.muted}>
                  {l.receiptNumber ? `Receipt ${l.receiptNumber}` : `Refund ${l.refundNumber}`}
                  {l.match === 'AmountDiffers' && l.recordedAmount !== null
                    ? ` records ${l.recordedAmount.toFixed(2)}`
                    : ''}
                </Caption1>
              )}
            </span>
          ),
        },
      ]}
      emptyTitle="No transactions of yours in this settlement"
      size="small"
    />
  );
}
