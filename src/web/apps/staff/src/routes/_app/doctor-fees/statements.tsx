import { Badge, Button, Caption1, Field, Input, makeStyles, Text, tokens } from '@fluentui/react-components';
import { DocumentTextRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateOnly, todayIso } from '@hc360/core';
import {
  DataTable,
  EmptyState,
  ErrorState,
  FilterBar,
  KpiCard,
  LoadingState,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
} from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { SERVICE_CATEGORY_LABEL } from '@/modules/billing/labels';
import { sendToPrinter } from '@/modules/billing/print';
import { dateParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { ACCRUAL_STATUS_LABEL, feeQueries, PAYOUT_STATUS_LABEL } from '@/modules/doctor-fees/api';
import { feeRate, isPeriod, monthStart } from '@/modules/doctor-fees/logic';
import { PractitionerPicker, type PickedDoctor } from '@/modules/doctor-fees/PractitionerPicker';
import { buildStatementHtml } from '@/modules/doctor-fees/print';

interface StatementSearch {
  from?: string;
  to?: string;
}

export const Route = createFileRoute('/_app/doctor-fees/statements')({
  validateSearch: (s: Record<string, unknown>): StatementSearch => ({ from: dateParam(s.from), to: dateParam(s.to) }),
  component: StatementsPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Doctor fee statement (DFA-04): what a doctor earned in a period, what was reversed, in payouts and paid; printable. */
function StatementsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const letterhead = useLetterhead();
  const canPayouts = usePermission('doctorfees.payouts.manage');
  const [doctor, setDoctor] = useState<PickedDoctor | null>(null);
  const from = search.from ?? monthStart(todayIso());
  const to = search.to ?? todayIso();
  const periodOk = isPeriod(from, to);
  const statement = useQuery({
    ...feeQueries.statement(doctor?.id ?? '', from, to),
    enabled: canPayouts && doctor !== null && periodOk,
  });

  if (!canPayouts) {
    return (
      <Page>
        <PageHeader title="Fee statements" />
        <NoAccess what="doctor fee statements" />
      </Page>
    );
  }

  const s = statement.data;
  const print = () => {
    if (s) {
      void sendToPrinter(buildStatementHtml(s, letterhead, new Date().toISOString()), 'A4').catch((error: unknown) =>
        notify.error(error, 'Printing failed'),
      );
    }
  };

  return (
    <Page>
      <PageHeader
        title="Fee statements"
        subtitle="A doctor's fees for a period, for sharing with the doctor"
        icon={<DocumentTextRegular />}
        actions={
          <Button appearance="primary" icon={<PrintRegular />} disabled={!s} onClick={print}>
            Print statement
          </Button>
        }
      />
      <Section>
        <FilterBar>
          <div style={{ flex: '1 1 280px', maxWidth: 420 }}>
            <PractitionerPicker value={doctor} onChange={setDoctor} required autoFocus />
          </div>
          <Field label="From">
            <Input
              type="date"
              value={from}
              max={todayIso()}
              onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, from: d.value || undefined }) })}
            />
          </Field>
          <Field
            label="To"
            validationMessage={periodOk ? undefined : 'The period must end on or after its start.'}
            validationState={periodOk ? 'none' : 'error'}
          >
            <Input
              type="date"
              value={to}
              max={todayIso()}
              onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, to: d.value || undefined }) })}
            />
          </Field>
        </FilterBar>
      </Section>
      {!doctor ? (
        <EmptyState
          icon={<DocumentTextRegular />}
          title="Choose a doctor"
          description="The statement lists every fee earned in the period, reversals and payouts."
        />
      ) : statement.isLoading ? (
        <LoadingState />
      ) : statement.error || !s ? (
        <ErrorState error={statement.error} onRetry={() => void statement.refetch()} />
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard
              label="Earned"
              value={<Amount value={s.accrued} />}
              hint={`${formatDateOnly(s.from)} – ${formatDateOnly(s.to)}`}
            />
            <KpiCard label="Reversed" value={<Amount value={s.reversed} />} hint="Cancelled bills" />
            <KpiCard label="In a payout" value={<Amount value={s.inPayout} />} />
            <KpiCard label="Paid out" value={<Amount value={s.paidOut} />} />
            <KpiCard label="Unpaid" value={<Amount value={s.unpaid} tone={s.unpaid > 0 ? 'negative' : undefined} />} />
          </div>
          <Section title="Fees by bill line" flush>
            <DataTable
              ariaLabel="Fees by bill line"
              rows={s.accruals}
              getRowId={(a) => a.id}
              size="small"
              columns={[
                { key: 'date', header: 'Earned on', width: 120, render: (a) => formatDateOnly(a.accruedOn) },
                {
                  key: 'service',
                  header: 'Service',
                  render: (a) => (
                    <span>
                      <Text>{a.description}</Text>{' '}
                      {a.isReversal && (
                        <Badge size="small" appearance="tint" color="danger">
                          Reversal
                        </Badge>
                      )}
                      <Caption1 block className={styles.muted}>
                        Bill {a.billNumber} · {SERVICE_CATEGORY_LABEL[a.category]}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'basis',
                  header: 'Billed value',
                  align: 'end',
                  priority: 'low',
                  render: (a) => <Amount value={a.basisAmount} />,
                },
                {
                  key: 'rate',
                  header: 'Rate',
                  align: 'end',
                  priority: 'low',
                  render: (a) => feeRate(a.method, a.rate),
                },
                {
                  key: 'share',
                  header: 'Share',
                  align: 'end',
                  render: (a) => (
                    <Amount value={a.shareAmount} strong tone={a.shareAmount < 0 ? 'negative' : undefined} />
                  ),
                },
                {
                  key: 'status',
                  header: 'Status',
                  render: (a) => <StatusBadge status={a.status} label={ACCRUAL_STATUS_LABEL[a.status]} />,
                },
              ]}
              emptyTitle="No fees in this period"
            />
          </Section>
          {s.payouts.length > 0 && (
            <Section title="Payouts" flush>
              <DataTable
                ariaLabel="Payouts in this period"
                rows={s.payouts}
                getRowId={(p) => p.id}
                size="small"
                columns={[
                  { key: 'no', header: 'Payout', render: (p) => <Text font="monospace">{p.payoutNumber}</Text> },
                  {
                    key: 'period',
                    header: 'Period',
                    render: (p) => `${formatDateOnly(p.periodFrom)} – ${formatDateOnly(p.periodTo)}`,
                  },
                  { key: 'net', header: 'Net', align: 'end', render: (p) => <Amount value={p.netAmount} strong /> },
                  {
                    key: 'status',
                    header: 'Status',
                    render: (p) => <StatusBadge status={p.status} label={PAYOUT_STATUS_LABEL[p.status]} />,
                  },
                ]}
              />
            </Section>
          )}
        </>
      )}
    </Page>
  );
}
