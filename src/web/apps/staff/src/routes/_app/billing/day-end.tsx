import {
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DocumentTableRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso, useDebouncedValue, useSession } from '@hc360/core';
import {
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
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { billingQueries } from '@/modules/billing/api';
import { useBillingLive } from '@/modules/billing/hooks';
import { PAYMENT_MODE_LABEL } from '@/modules/billing/labels';
import { buildDayEndHtml, sendToPrinter } from '@/modules/billing/print';
import { ShiftStatusBadge } from '@/modules/billing/ShiftControls';
import { dateParam, textParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';

interface DayEndSearch {
  date?: string;
  counter?: string;
}

export const Route = createFileRoute('/_app/billing/day-end')({
  validateSearch: (s: Record<string, unknown>): DayEndSearch => ({
    date: dateParam(s.date),
    counter: textParam(s.counter, 64),
  }),
  component: DayEndPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))' },
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 2fr)',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Day-end collection report (OPB-07): totals by payment mode and by cashier, bills, discounts and cancellations; printable. */
function DayEndPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const letterhead = useLetterhead();
  const printedBy = useSession((s) => s.user?.displayName) ?? '';
  const canShifts = usePermission('billing.shifts');
  const [counter, setCounter] = useState(search.counter ?? '');
  const debouncedCounter = useDebouncedValue(counter.trim(), 400);
  useBillingLive();
  const date = search.date ?? todayIso();
  const report = useQuery({ ...billingQueries.dayEnd(date, debouncedCounter || undefined), enabled: canShifts });

  if (!canShifts) {
    return (
      <Page>
        <PageHeader title="Day-end report" />
        <NoAccess what="the day-end report" />
      </Page>
    );
  }

  const r = report.data;
  const print = () => {
    if (r) {
      void sendToPrinter(buildDayEndHtml(r, letterhead, printedBy, new Date().toISOString()), 'A4').catch(
        (error: unknown) => notify.error(error, 'Printing failed'),
      );
    }
  };

  return (
    <Page>
      <PageHeader
        title="Day-end report"
        subtitle={`${formatDateOnly(date)}${r?.counter ? ` · ${r.counter}` : ' · all counters'}`}
        icon={<DocumentTableRegular />}
        actions={
          <Button appearance="primary" icon={<PrintRegular />} onClick={print} disabled={!r}>
            Print
          </Button>
        }
      />
      <Section>
        <FilterBar>
          <Field label="Date">
            <Input
              type="date"
              value={date}
              max={todayIso()}
              onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, date: d.value || undefined }) })}
            />
          </Field>
          <Field label="Counter" hint="Leave empty for all counters">
            <Input
              value={counter}
              onChange={(_, d) => {
                setCounter(d.value);
                void navigate({ search: (prev) => ({ ...prev, counter: d.value.trim() || undefined }), replace: true });
              }}
              placeholder="e.g. OPD Counter 1"
            />
          </Field>
        </FilterBar>
      </Section>

      {report.isLoading ? (
        <LoadingState label="Adding up the day…" />
      ) : report.error || !r ? (
        <ErrorState error={report.error} onRetry={() => void report.refetch()} />
      ) : (
        <>
          {(r.shortagesAwaiting ?? 0) > 0 && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <MessageBarTitle>
                  {r.shortagesAwaiting} shift{r.shortagesAwaiting === 1 ? '' : 's'} closed short, the shortage awaiting
                  a supervisor
                </MessageBarTitle>
                A cash shortage is not settled on the cashier&rsquo;s word: a supervisor other than the cashier accepts
                it in Approvals. An excess is recorded and needs no one.
              </MessageBarBody>
            </MessageBar>
          )}
          {r.openShifts > 0 && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <MessageBarTitle>
                  {r.openShifts} shift{r.openShifts === 1 ? ' is' : 's are'} still open
                </MessageBarTitle>
                The figures can still change until every cashier closes their shift.
              </MessageBarBody>
            </MessageBar>
          )}
          <div className={styles.kpis}>
            <KpiCard
              label="Net collection"
              value={<Amount value={r.netCollection} />}
              hint={`Collected ${formatInr(r.totalCollected)} · refunded ${formatInr(r.totalRefunded)}`}
            />
            {(r.notReceived?.length ?? 0) + (r.notPaidOut?.length ?? 0) > 0 && (
              <KpiCard
                label="Net, with the day's reversals"
                value={<Amount value={r.netAfterReversals ?? r.netCollection} />}
                hint={`Not received ${formatInr((r.notReceived ?? []).reduce((sum, t) => sum + t.amount, 0))} · not paid out ${formatInr((r.notPaidOut ?? []).reduce((sum, t) => sum + t.amount, 0))}`}
              />
            )}
            <KpiCard label="IP advances collected" value={<Amount value={r.depositsCollected} />} />
            <KpiCard
              label="Bills finalised"
              value={r.billsFinalized.toLocaleString('en-IN')}
              hint={`Billed ${formatInr(r.billedAmount)}`}
            />
            <KpiCard label="Discounts given" value={<Amount value={r.discountGiven} />} />
            <KpiCard
              label="Bills cancelled"
              value={r.billsCancelled.toLocaleString('en-IN')}
              hint={r.billsCancelled ? formatInr(r.cancelledAmount) : undefined}
            />
          </div>
          <div className={styles.grid}>
            <Section title="By payment mode" flush>
              <DataTable
                ariaLabel="Collections by payment mode"
                rows={r.byMode}
                getRowId={(m) => m.mode}
                size="small"
                columns={[
                  { key: 'mode', header: 'Mode', render: (m) => PAYMENT_MODE_LABEL[m.mode] },
                  {
                    key: 'collected',
                    header: 'Collected',
                    align: 'end',
                    render: (m) => <Amount value={m.collected} />,
                  },
                  { key: 'refunded', header: 'Refunded', align: 'end', render: (m) => <Amount value={m.refunded} /> },
                  { key: 'net', header: 'Net', align: 'end', render: (m) => <Amount value={m.net} strong /> },
                ]}
                emptyTitle="Nothing collected"
              />
            </Section>
            {(r.notReceived?.length ?? 0) + (r.notPaidOut?.length ?? 0) > 0 && (
              <Section title="Reversed today" flush>
                <DataTable
                  ariaLabel="Money reversed today as not received or not paid out"
                  rows={[
                    ...(r.notReceived ?? []).map((t) => ({ ...t, kind: 'Not received' as const })),
                    ...(r.notPaidOut ?? []).map((t) => ({ ...t, kind: 'Refund not paid out' as const })),
                  ]}
                  getRowId={(t) => `${t.kind}-${t.mode}`}
                  size="small"
                  columns={[
                    { key: 'kind', header: 'What', render: (t) => t.kind },
                    { key: 'mode', header: 'Mode', render: (t) => PAYMENT_MODE_LABEL[t.mode] },
                    { key: 'count', header: 'Records', align: 'end', render: (t) => t.count.toLocaleString('en-IN') },
                    {
                      key: 'amount',
                      header: 'Amount',
                      align: 'end',
                      render: (t) => <Amount value={t.amount} strong />,
                    },
                  ]}
                />
              </Section>
            )}
            <Section title="By cashier" flush>
              <DataTable
                ariaLabel="Collections by cashier"
                rows={r.shifts}
                getRowId={(s) => s.shiftId}
                size="small"
                columns={[
                  {
                    key: 'cashier',
                    header: 'Cashier',
                    render: (s) => (
                      <span>
                        <Text weight="semibold">{s.userName}</Text>
                        <Text block size={200} className={styles.muted}>
                          {s.counter} · {formatDateTime(s.openedAt)}
                        </Text>
                      </span>
                    ),
                  },
                  {
                    key: 'collected',
                    header: 'Collected',
                    align: 'end',
                    render: (s) => <Amount value={s.collected} />,
                  },
                  {
                    key: 'refunded',
                    header: 'Refunded',
                    align: 'end',
                    priority: 'low',
                    render: (s) => <Amount value={s.refunded} />,
                  },
                  {
                    key: 'expected',
                    header: 'Expected cash',
                    align: 'end',
                    render: (s) => <Amount value={s.expectedCash} />,
                  },
                  {
                    key: 'declared',
                    header: 'Declared',
                    align: 'end',
                    render: (s) => <Amount value={s.declaredCash} />,
                  },
                  {
                    key: 'variance',
                    header: 'Short / excess',
                    align: 'end',
                    render: (s) =>
                      s.variance === null ? (
                        '—'
                      ) : (
                        <Amount
                          value={s.variance}
                          strong
                          tone={s.variance < 0 ? 'negative' : s.variance > 0 ? 'neutral' : 'positive'}
                        />
                      ),
                  },
                  { key: 'status', header: 'Shift', priority: 'low', render: (s) => <ShiftStatusBadge shift={s} /> },
                ]}
                emptyTitle="No shifts on this day"
              />
            </Section>
          </div>
        </>
      )}
    </Page>
  );
}
