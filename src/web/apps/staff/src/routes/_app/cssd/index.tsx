import { Button, Field, Input, makeStyles, Text, tokens } from '@fluentui/react-components';
import {
  BoxArrowUpRegular,
  BoxCheckmarkRegular,
  ClipboardTaskRegular,
  ClockAlarmRegular,
  ErrorCircleRegular,
  FireRegular,
  TimerRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, todayIso } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { usePermission } from '@/lib/permissions';
import { cssdQueries, type CssdSummary } from '@/modules/cssd/api';
import { METHOD_LABEL } from '@/modules/cssd/rules';
import { addDays, daysBetween } from '@/modules/inpatient/time';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/cssd/')({
  validateSearch: (s: Record<string, unknown>): { from?: string; to?: string } => ({
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
  }),
  component: CssdOverviewPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))' },
  columns: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 3fr)',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  low: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

/** CSSD dashboard (CSSD-01..05): loads and failures, indicators pending, requests, discrepancies, recalls and stock. */
function CssdOverviewPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canProcess = usePermission('cssd.process');
  const canRequest = usePermission('cssd.requests');
  const today = todayIso();
  let to = search.to && search.to <= today ? search.to : today;
  let from = search.from ?? addDays(to, -29);
  if (from > to) {
    [from, to] = [to, from];
  }
  if (daysBetween(from, to) > 366) {
    from = addDays(to, -366);
  }
  const summary = useQuery(cssdQueries.summary(from, to));
  const s = summary.data;
  const value = (n: number | undefined) => (n === undefined ? '…' : n.toLocaleString('en-IN'));

  return (
    <Page>
      <PageHeader
        title="CSSD"
        icon={<BoxCheckmarkRegular />}
        subtitle={`Central sterile supply · ${formatDateOnly(from)} to ${formatDateOnly(to)}`}
        actions={
          <>
            {canRequest && (
              <Button icon={<ClipboardTaskRegular />} onClick={() => void navigate({ to: '/cssd/requests' })}>
                Requests
              </Button>
            )}
            {canProcess && (
              <Button
                appearance="primary"
                icon={<BoxArrowUpRegular />}
                onClick={() => void navigate({ to: '/cssd/issue' })}
              >
                Issue packs
              </Button>
            )}
          </>
        }
      />
      <Section>
        <FilterBar>
          <Field label="From">
            <Input
              type="date"
              value={from}
              max={to}
              onChange={(_, d) =>
                d.value && void navigate({ search: (prev) => ({ ...prev, from: d.value }), replace: true })
              }
            />
          </Field>
          <Field label="To">
            <Input
              type="date"
              value={to}
              min={from}
              max={today}
              onChange={(_, d) =>
                d.value && void navigate({ search: (prev) => ({ ...prev, to: d.value }), replace: true })
              }
            />
          </Field>
        </FilterBar>
      </Section>

      {summary.error && !s ? (
        <ErrorState
          error={summary.error}
          onRetry={() => void summary.refetch()}
          title="Could not load the CSSD summary"
        />
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard
              label="Loads"
              icon={<FireRegular />}
              value={value(s?.loads)}
              hint={s ? `${s.loadsReleased} released` : undefined}
              onClick={() => void navigate({ to: '/cssd/loads' })}
            />
            <KpiCard
              label="Failed loads"
              icon={<ErrorCircleRegular />}
              value={s ? `${s.failureRatePercent}%` : '…'}
              hint={s ? `${s.loadsFailed} failed` : undefined}
              onClick={() => void navigate({ to: '/cssd/loads', search: { status: 'Failed' } })}
            />
            <KpiCard
              label="Biological indicators to read"
              icon={<TimerRegular />}
              value={value(s?.biologicalPending)}
              onClick={() => void navigate({ to: '/cssd/loads', search: { biologicalPending: true } })}
            />
            <KpiCard
              label="Packs issued"
              icon={<BoxArrowUpRegular />}
              value={value(s?.packsIssued)}
              onClick={() => void navigate({ to: '/cssd/issues' })}
            />
            <KpiCard
              label="Open requests"
              icon={<ClipboardTaskRegular />}
              value={value(s?.openRequests)}
              onClick={() => void navigate({ to: '/cssd/requests' })}
            />
            <KpiCard
              label="Count differences"
              icon={<WarningRegular />}
              value={value(s?.receiptsWithDiscrepancy)}
              hint="Soiled receipts"
              onClick={() => void navigate({ to: '/cssd/receipts', search: { discrepancies: true } })}
            />
            <KpiCard
              label="Open recalls"
              icon={<ErrorCircleRegular />}
              value={value(s?.openRecalls)}
              onClick={() => void navigate({ to: '/cssd/recalls' })}
            />
            <KpiCard
              label="Expiring in 2 days"
              icon={<ClockAlarmRegular />}
              value={value(s?.packsExpiringSoon)}
              hint="Issue these first"
              onClick={() => void navigate({ to: '/cssd/packs', search: { expiring: true } })}
            />
          </div>
          <div className={styles.columns}>
            <Section title="Loads by method">
              {s ? (
                s.byMethod.length ? (
                  <MethodChart summary={s} />
                ) : (
                  <Text>No loads in this period.</Text>
                )
              ) : (
                <Text>Loading…</Text>
              )}
            </Section>
            <Section
              title="Sterile stock by set"
              description="Packs on the shelf that are within their sterile date"
              flush
            >
              <DataTable
                ariaLabel="Sterile stock by set"
                rows={s?.availableBySet}
                loading={summary.isLoading}
                getRowId={(r) => r.instrumentSetId}
                onRowClick={(r) => void navigate({ to: '/cssd/packs', search: { setId: r.instrumentSetId } })}
                size="small"
                columns={[
                  { key: 'set', header: 'Set', render: (r) => `${r.name} (${r.code})` },
                  {
                    key: 'available',
                    header: 'On shelf',
                    width: 100,
                    align: 'end',
                    render: (r) => <span className={r.available === 0 ? styles.low : undefined}>{r.available}</span>,
                  },
                  {
                    key: 'expiring',
                    header: 'Expiring soon',
                    width: 130,
                    align: 'end',
                    render: (r) => r.expiringSoon || '—',
                  },
                ]}
                emptyTitle="No sets set up"
                emptyDescription="Add instrument sets and their count sheets first."
              />
            </Section>
          </div>
        </>
      )}
    </Page>
  );
}

function MethodChart({ summary }: { summary: CssdSummary }) {
  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 40, right: 16, top: 32, bottom: 32 },
      tooltip: { trigger: 'axis' },
      legend: { top: 0 },
      xAxis: { type: 'category', data: summary.byMethod.map((m) => METHOD_LABEL[m.method] ?? m.method) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        {
          type: 'bar',
          name: 'Passed',
          stack: 'loads',
          data: summary.byMethod.map((m) => m.loads - m.failed),
          itemStyle: { color: '#0A7CA7' },
        },
        {
          type: 'bar',
          name: 'Failed',
          stack: 'loads',
          data: summary.byMethod.map((m) => m.failed),
          itemStyle: { color: '#C50F1F' },
        },
      ],
    }),
    [summary],
  );
  return (
    <EChart
      option={option}
      height={240}
      ariaLabel={`Loads by method: ${summary.byMethod.map((m) => `${m.method} ${m.loads} (${m.failed} failed)`).join(', ')}`}
    />
  );
}
