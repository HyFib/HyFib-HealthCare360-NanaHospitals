import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import {
  AddRegular,
  AlertUrgentRegular,
  CalendarClockRegular,
  CalendarErrorRegular,
  ClockRegular,
  GaugeRegular,
  HandshakeRegular,
  MoneyRegular,
  ShieldCheckmarkRegular,
  WrenchRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatInr, formatRelative } from '@hc360/core';
import { DataTable, ErrorState, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { usePermission } from '@/lib/permissions';
import { assetQueries, type AssetDashboard } from '@/modules/assets/api';
import { AssetDrawer } from '@/modules/assets/AssetDrawer';
import { BreakdownDialog } from '@/modules/assets/BreakdownDialog';
import { assetStatusLabel, dueKindLabel } from '@/modules/assets/labels';
import { dueLabel } from '@/modules/assets/ticketFlow';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { DocStatus } from '@/modules/inventory/StockBits';

export const Route = createFileRoute('/_app/assets/')({
  component: AssetDashboardPage,
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
  late: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

/** Biomedical engineering dashboard: fleet status, breakdowns, and what falls due. */
function AssetDashboardPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canManage = usePermission(S.assetsManage);
  const [reporting, setReporting] = useState(false);
  const [adding, setAdding] = useState(false);
  const dashboard = useQuery(assetQueries.dashboard());
  const due = useQuery(assetQueries.due(30));
  const tickets = useQuery(assetQueries.tickets({ openOnly: true }));
  const d = dashboard.data;
  const n = (value: number | undefined) => (value === undefined ? '…' : value.toLocaleString('en-IN'));

  return (
    <Page>
      <PageHeader
        title="Equipment and assets"
        subtitle="Breakdowns, preventive maintenance, calibration and contracts"
        actions={
          <>
            {canManage && (
              <Button icon={<AddRegular />} onClick={() => setAdding(true)}>
                Add asset
              </Button>
            )}
            <Button appearance="primary" icon={<WrenchRegular />} onClick={() => setReporting(true)}>
              Report breakdown
            </Button>
          </>
        }
      />
      {dashboard.error && !d ? (
        <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} />
      ) : (
        <div className={styles.kpis}>
          <KpiCard
            label="Assets"
            value={n(d?.totalAssets)}
            icon={<GaugeRegular />}
            onClick={() => void navigate({ to: '/assets/register' })}
          />
          <KpiCard
            label="Open breakdowns"
            value={n(d?.openTickets)}
            icon={<WrenchRegular />}
            hint={d && d.criticalOpenTickets > 0 ? `${d.criticalOpenTickets} critical` : undefined}
            onClick={() => void navigate({ to: '/assets/tickets' })}
          />
          <KpiCard
            label="PM overdue"
            value={n(d?.pmOverdue)}
            icon={<CalendarErrorRegular />}
            hint={d ? `${d.pmDueThisWeek} due this week` : undefined}
            onClick={() => void navigate({ to: '/assets/maintenance' })}
          />
          <KpiCard
            label="Calibration due"
            value={n(d?.calibrationDue)}
            icon={<ShieldCheckmarkRegular />}
            onClick={() => void navigate({ to: '/assets/maintenance' })}
          />
          <KpiCard
            label="Warranties ending"
            value={n(d?.warrantyExpiring)}
            icon={<CalendarClockRegular />}
            hint="In the next 30 days"
          />
          <KpiCard
            label="Contracts ending"
            value={n(d?.contractsExpiring)}
            icon={<HandshakeRegular />}
            hint="AMC / CMC, next 30 days"
          />
          <KpiCard
            label="Downtime this month"
            value={d ? `${Math.round(d.downtimeHoursThisMonth).toLocaleString('en-IN')} h` : '…'}
            icon={<ClockRegular />}
          />
          <KpiCard
            label="Book value"
            value={d ? formatInr(d.bookValue, { whole: true }) : '…'}
            icon={<MoneyRegular />}
          />
        </div>
      )}

      <div className={styles.grid}>
        <Section title="Fleet status">
          {d ? <StatusChart dashboard={d} /> : <Text className={styles.muted}>Loading…</Text>}
        </Section>
        <Section
          title="Falling due in 30 days"
          description="Preventive maintenance, calibration, warranties and AMC/CMC contracts"
          flush
        >
          <DataTable
            ariaLabel="Falling due"
            rows={due.data?.slice(0, 12)}
            loading={due.isLoading}
            error={due.error}
            onRetry={() => void due.refetch()}
            getRowId={(i) => `${i.kind}-${i.assetId}-${i.dueDate}-${i.title}`}
            onRowClick={(i) => void navigate({ to: '/assets/$assetId', params: { assetId: i.assetId } })}
            size="small"
            columns={[
              {
                key: 'asset',
                header: 'Equipment',
                render: (i) => (
                  <span>
                    <Text weight="semibold">{i.assetName}</Text>{' '}
                    <Caption1 className={styles.muted}>{i.assetTag}</Caption1>
                  </span>
                ),
              },
              { key: 'what', header: 'What', render: (i) => `${dueKindLabel[i.kind] ?? i.kind}: ${i.title}` },
              {
                key: 'when',
                header: 'When',
                render: (i) => (
                  <span className={i.daysToDue < 0 ? styles.late : undefined}>
                    {dueLabel(i.daysToDue)}
                    <Caption1 block className={styles.muted}>
                      {formatDateOnly(i.dueDate)}
                    </Caption1>
                  </span>
                ),
              },
            ]}
            emptyTitle="Nothing falls due in the next 30 days"
          />
        </Section>
      </div>

      <Section title="Open breakdowns" flush>
        <DataTable
          ariaLabel="Open breakdowns"
          rows={tickets.data?.items}
          loading={tickets.isLoading}
          error={tickets.error}
          onRetry={() => void tickets.refetch()}
          getRowId={(t) => t.id}
          onRowClick={(t) => void navigate({ to: '/assets/tickets', search: { id: t.id } })}
          size="small"
          columns={[
            {
              key: 'priority',
              header: 'Priority',
              width: 100,
              render: (t) =>
                t.priority === 'Critical' ? (
                  <Badge color="danger" icon={<AlertUrgentRegular />}>
                    Critical
                  </Badge>
                ) : (
                  <Badge appearance="tint" color={t.priority === 'High' ? 'warning' : 'informative'}>
                    {t.priority}
                  </Badge>
                ),
            },
            {
              key: 'asset',
              header: 'Equipment',
              render: (t) => (
                <span>
                  <Text weight="semibold">{t.assetName}</Text>{' '}
                  <Caption1 className={styles.muted}>{t.assetTag}</Caption1>
                </span>
              ),
            },
            { key: 'issue', header: 'Problem', render: (t) => t.issue },
            { key: 'reported', header: 'Reported', priority: 'low', render: (t) => formatRelative(t.reportedAt) },
            { key: 'assigned', header: 'With', priority: 'low', render: (t) => t.assignedTo ?? 'Not assigned' },
            { key: 'status', header: 'Status', render: (t) => <DocStatus status={t.status} /> },
          ]}
          emptyTitle="No open breakdowns"
          emptyDescription="All equipment reported working."
        />
      </Section>

      {reporting && (
        <BreakdownDialog
          onClose={() => setReporting(false)}
          onReported={(id) => void navigate({ to: '/assets/tickets', search: { id } })}
        />
      )}
      {adding && (
        <AssetDrawer
          asset={null}
          onClose={() => setAdding(false)}
          onCreated={(id) => void navigate({ to: '/assets/$assetId', params: { assetId: id } })}
        />
      )}
    </Page>
  );
}

function StatusChart({ dashboard }: { dashboard: AssetDashboard }) {
  const option = useMemo<ChartOption>(
    () => ({
      tooltip: { trigger: 'item' },
      legend: { bottom: 0 },
      series: [
        {
          type: 'pie',
          name: 'Assets',
          radius: ['40%', '70%'],
          data: dashboard.byStatus.map((s) => ({ name: assetStatusLabel[s.status] ?? s.status, value: s.count })),
          label: { formatter: '{b}: {c}' },
        },
      ],
    }),
    [dashboard],
  );
  return <EChart option={option} height={260} ariaLabel="Assets by status" />;
}
