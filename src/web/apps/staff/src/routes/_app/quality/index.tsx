import { Button, Caption1, Field, makeStyles, Select, Text, tokens } from '@fluentui/react-components';
import {
  AlertUrgentRegular,
  ClipboardTaskRegular,
  DataTrendingRegular,
  GridRegular,
  ShieldCheckmarkRegular,
  ShieldErrorRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, todayIso } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, KpiCard, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { usePermission } from '@/lib/permissions';
import { EChart, type ChartOption } from '@/lib/EChart';
import { addMonths, monthKeyLabel } from '@/modules/hr/shared/dates';
import { Columns, KpiGrid, Stack } from '@/modules/hr/shared/layout';
import { qualityQueries, type QualityDashboard } from '@/modules/quality/api';
import { RatingBadge } from '@/modules/quality/badges';
import {
  incidentStatusLabel,
  incidentTypeLabel,
  QUALITY_MANAGERS,
  QUALITY_PERMISSIONS as Q,
  severityLabel,
  type IncidentSeverity,
  type IncidentStatus,
  type IncidentType,
} from '@/modules/quality/labels';

type Period = '3m' | '12m' | 'fy';

export const Route = createFileRoute('/_app/quality/')({
  validateSearch: (s: Record<string, unknown>): { period?: Period } => ({
    period: s.period === '3m' || s.period === 'fy' ? s.period : undefined,
  }),
  component: QualityHome,
});

const useStyles = makeStyles({
  landing: { display: 'flex', gap: '12px', flexWrap: 'wrap', justifyContent: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function QualityHome() {
  const isManager = usePermission(QUALITY_MANAGERS);
  return isManager ? <DashboardPage /> : <StaffLanding />;
}

/** Staff who only report incidents see the two things they can do here. */
function StaffLanding() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canReport = usePermission(Q.incidentsReport);
  return (
    <Page maxWidth={900}>
      <PageHeader title="Quality and safety" icon={<ShieldCheckmarkRegular />} />
      <Section>
        <EmptyState
          icon={<ShieldErrorRegular />}
          title="Help us keep patients and staff safe"
          description={
            canReport
              ? 'Report anything that went wrong or nearly went wrong. You can stay anonymous.'
              : 'You do not have access to the quality screens.'
          }
          action={
            canReport ? (
              <div className={styles.landing}>
                <Button
                  appearance="primary"
                  icon={<ShieldErrorRegular />}
                  onClick={() => void navigate({ to: '/quality/report' })}
                >
                  Report an incident
                </Button>
                <Button icon={<ClipboardTaskRegular />} onClick={() => void navigate({ to: '/quality/my-reports' })}>
                  My reports and actions
                </Button>
              </div>
            ) : undefined
          }
        />
      </Section>
    </Page>
  );
}

function periodRange(period: Period): { from: string; to: string; label: string } {
  const today = todayIso();
  if (period === '3m') {
    return { from: addMonths(today, -3), to: today, label: 'last 3 months' };
  }
  if (period === 'fy') {
    const [year = 2000, month = 1] = today.split('-').map(Number);
    const start = month >= 4 ? year : year - 1;
    return { from: `${start}-04-01`, to: today, label: `financial year ${start}–${String(start + 1).slice(2)}` };
  }
  return { from: `${addMonths(today, -11).slice(0, 7)}-01`, to: today, label: 'last 12 months' };
}

function DashboardPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canIncidents = usePermission(Q.incidentsManage);
  const canCapa = usePermission(Q.capaManage);
  const canRisks = usePermission(Q.risksManage);
  const canIndicators = usePermission(Q.indicatorsManage);
  const period = search.period ?? '12m';
  const range = periodRange(period);
  const dashboard = useQuery(qualityQueries.dashboard(range.from, range.to));

  return (
    <Page>
      <PageHeader
        title="Quality dashboard"
        subtitle={`Incidents, corrective actions, risks and NABH indicators · ${range.label}`}
        icon={<ShieldCheckmarkRegular />}
        actions={
          <>
            <Field label="Period" orientation="horizontal">
              <Select
                value={period}
                onChange={(_, d) =>
                  void navigate({ search: { period: d.value === '12m' ? undefined : (d.value as Period) } })
                }
              >
                <option value="3m">Last 3 months</option>
                <option value="12m">Last 12 months</option>
                <option value="fy">This financial year</option>
              </Select>
            </Field>
            <Button
              appearance="primary"
              icon={<ShieldErrorRegular />}
              onClick={() => void navigate({ to: '/quality/report' })}
            >
              Report an incident
            </Button>
          </>
        }
      />
      {dashboard.isLoading && <LoadingState label="Gathering the quality figures…" />}
      {dashboard.error && <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} />}
      {dashboard.data && (
        <DashboardBody
          d={dashboard.data}
          can={{ incidents: canIncidents, capa: canCapa, risks: canRisks, indicators: canIndicators }}
        />
      )}
    </Page>
  );
}

function DashboardBody({
  d,
  can,
}: {
  d: QualityDashboard;
  can: { incidents: boolean; capa: boolean; risks: boolean; indicators: boolean };
}) {
  const styles = useStyles();
  const navigate = useNavigate();

  const monthOption = useMemo<ChartOption>(
    () => ({
      grid: { left: 40, right: 16, top: 32, bottom: 32 },
      tooltip: { trigger: 'axis' },
      legend: { top: 0 },
      xAxis: { type: 'category', data: d.byMonth.map((m) => monthKeyLabel(m.month)) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        { type: 'bar', name: 'Incidents', data: d.byMonth.map((m) => m.total), itemStyle: { color: '#0A7CA7' } },
        { type: 'bar', name: 'Sentinel', data: d.byMonth.map((m) => m.sentinel), itemStyle: { color: '#D13438' } },
      ],
    }),
    [d.byMonth],
  );

  const severityOption = useMemo<ChartOption>(() => {
    const order: IncidentSeverity[] = ['NearMiss', 'NoHarm', 'Mild', 'Moderate', 'Severe', 'Death'];
    const colors = ['#8A8886', '#2E9E5B', '#C19C00', '#DA7B00', '#D13438', '#750B1C'];
    const counts = order.map((s) => d.bySeverity.find((x) => x.key === s)?.count ?? 0);
    return {
      grid: { left: 110, right: 24, top: 8, bottom: 24 },
      tooltip: { trigger: 'axis' },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: { type: 'category', data: order.map((s) => severityLabel[s]), inverse: true },
      series: [
        {
          type: 'bar',
          name: 'Incidents',
          data: counts.map((value, index) => ({ value, itemStyle: { color: colors[index] } })),
        },
      ],
    };
  }, [d.bySeverity]);

  const typeOption = useMemo<ChartOption>(() => {
    const sorted = [...d.byType].sort((a, b) => b.count - a.count).slice(0, 8);
    return {
      grid: { left: 180, right: 24, top: 8, bottom: 24 },
      tooltip: { trigger: 'axis' },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: {
        type: 'category',
        data: sorted.map((t) => incidentTypeLabel[t.key as IncidentType] ?? t.key),
        inverse: true,
      },
      series: [{ type: 'bar', name: 'Incidents', data: sorted.map((t) => t.count), itemStyle: { color: '#0A7CA7' } }],
    };
  }, [d.byType]);

  return (
    <Stack gap={16}>
      <KpiGrid>
        <KpiCard
          label="Incidents reported"
          value={d.totalIncidents}
          icon={<ShieldErrorRegular />}
          onClick={
            can.incidents ? () => void navigate({ to: '/quality/incidents', search: { view: 'all' } }) : undefined
          }
        />
        <KpiCard
          label="Sentinel events"
          value={d.sentinelEvents}
          icon={<AlertUrgentRegular />}
          hint="Root-cause analysis required"
          onClick={
            can.incidents
              ? () => void navigate({ to: '/quality/incidents', search: { sentinelOnly: true, view: 'all' } })
              : undefined
          }
        />
        <KpiCard
          label="Open incidents"
          value={d.openIncidents}
          icon={<ClipboardTaskRegular />}
          onClick={can.incidents ? () => void navigate({ to: '/quality/incidents' }) : undefined}
        />
        <KpiCard
          label="Actions overdue"
          value={d.capas.overdue}
          icon={<WarningRegular />}
          hint={`${d.capas.open + d.capas.inProgress} open · ${d.capas.awaitingVerification} awaiting check`}
          onClick={can.capa ? () => void navigate({ to: '/quality/capas', search: { overdueOnly: true } }) : undefined}
        />
        <KpiCard
          label="Risks due for review"
          value={d.risksDueForReview}
          icon={<GridRegular />}
          onClick={
            can.risks ? () => void navigate({ to: '/quality/risks', search: { reviewOverdue: true } }) : undefined
          }
        />
        <KpiCard
          label="Indicators off target"
          value={d.indicatorsOffTarget.length}
          icon={<DataTrendingRegular />}
          onClick={can.indicators ? () => void navigate({ to: '/quality/indicators/scorecard' }) : undefined}
        />
      </KpiGrid>

      <Section title="Incidents by month" description="All reports, with sentinel events in red">
        {d.byMonth.every((m) => m.total === 0) ? (
          <Caption1 className={styles.muted}>No incidents in this period.</Caption1>
        ) : (
          <EChart option={monthOption} height={260} ariaLabel="Incidents per month with sentinel events" />
        )}
      </Section>

      <Columns>
        <Section title="By seriousness">
          <EChart option={severityOption} height={220} ariaLabel="Incidents by seriousness" />
        </Section>
        <Section title="By kind" description="The eight most frequent">
          {d.byType.length === 0 ? (
            <Caption1 className={styles.muted}>No incidents in this period.</Caption1>
          ) : (
            <EChart option={typeOption} height={220} ariaLabel="Incidents by kind" />
          )}
        </Section>
      </Columns>

      <Columns>
        <Section title="Open incidents by stage" flush>
          <DataTable
            ariaLabel="Open incidents by stage"
            rows={d.openByStatus}
            getRowId={(r) => r.key}
            onRowClick={
              can.incidents
                ? (r) => void navigate({ to: '/quality/incidents', search: { status: r.key as IncidentStatus } })
                : undefined
            }
            columns={[
              { key: 'stage', header: 'Stage', render: (r) => incidentStatusLabel[r.key as IncidentStatus] ?? r.key },
              {
                key: 'count',
                header: 'Incidents',
                align: 'end',
                render: (r) => <Text weight="semibold">{r.count}</Text>,
              },
            ]}
            emptyTitle="No open incidents"
            size="small"
          />
        </Section>
        <Section title="Most overdue actions" flush>
          <DataTable
            ariaLabel="Most overdue actions"
            rows={d.overdueCapas}
            getRowId={(c) => c.id}
            onRowClick={(c) => void navigate({ to: '/quality/capas/$capaId', params: { capaId: c.id } })}
            columns={[
              {
                key: 'action',
                header: 'Action',
                render: (c) => (
                  <span>
                    <Text weight="semibold">{c.action}</Text>
                    <Caption1 block>
                      {c.capaNumber} · {c.ownerName ?? 'No owner'}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'late',
                header: 'Overdue by',
                width: 110,
                render: (c) => `${c.daysOverdue} day${c.daysOverdue === 1 ? '' : 's'}`,
              },
            ]}
            emptyTitle="Nothing overdue"
            size="small"
          />
        </Section>
      </Columns>

      <Columns>
        <Section title="Highest risks" description="By residual, then inherent score" flush>
          <DataTable
            ariaLabel="Highest risks"
            rows={d.topRisks}
            getRowId={(r) => r.id}
            onRowClick={can.risks ? () => void navigate({ to: '/quality/risks' }) : undefined}
            columns={[
              {
                key: 'risk',
                header: 'Risk',
                render: (r) => (
                  <span>
                    <Text weight="semibold">{r.title}</Text>
                    <Caption1 block>
                      {r.riskNumber} · review {formatDateOnly(r.reviewDate)}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'score',
                header: 'Score',
                render: (r) => <RatingBadge rating={r.rating} score={r.residualScore ?? r.score} />,
              },
            ]}
            emptyTitle="No open risks"
            size="small"
          />
        </Section>
        <Section title="Indicators off target" description="Latest recorded period" flush>
          <DataTable
            ariaLabel="Indicators off target"
            rows={d.indicatorsOffTarget}
            getRowId={(i) => i.id}
            onRowClick={
              can.indicators
                ? (i) => void navigate({ to: '/quality/indicators/$indicatorId', params: { indicatorId: i.id } })
                : undefined
            }
            columns={[
              {
                key: 'name',
                header: 'Indicator',
                render: (i) => (
                  <span>
                    <Text weight="semibold">{i.name}</Text>
                    <Caption1 block>
                      {i.code} · {i.periodLabel}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'value',
                header: 'Value / target',
                render: (i) =>
                  `${i.value.toLocaleString('en-IN', { maximumFractionDigits: 2 })} / ${i.target === null ? '—' : i.target.toLocaleString('en-IN')} (${i.direction === 'HigherIsBetter' ? 'higher' : 'lower'} is better)`,
              },
            ]}
            emptyTitle="Every indicator met its target"
            size="small"
          />
        </Section>
      </Columns>
    </Stack>
  );
}
