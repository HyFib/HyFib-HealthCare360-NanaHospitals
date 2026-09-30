import { Field, makeStyles, Select, Text, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular, ClockRegular, DataBarVerticalRegular, TimerRegular } from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { DataTable, ErrorState, KpiCard, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { otQueries, type OtUtilisationRow } from './api';
import { utilisationTotals } from './logic';
import { formatDuration } from './shared/datetime';

const useStyles = makeStyles({
  kpis: {
    display: 'grid',
    gap: '12px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))',
    marginBottom: '12px',
  },
  controls: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const pct = (value: number | null | undefined) =>
  value === null || value === undefined ? '—' : `${value.toLocaleString('en-IN', { maximumFractionDigits: 1 })}%`;

/**
 * Theatre utilisation (OT-07): actual theatre time against the planned OT session, first cases on time
 * (in the room within 15 minutes) and turnover between cases — the figures the OT committee reviews.
 */
export function UtilisationPanel({
  from,
  to,
  periodLabel,
  theatreId,
}: {
  from: string;
  to: string;
  periodLabel: string;
  theatreId?: string;
}) {
  const styles = useStyles();
  const [minutesPerDay, setMinutesPerDay] = useState(480);
  const report = useQuery(otQueries.utilisation({ from, to, theatreId, minutesPerDay }));
  const rows = useMemo(() => report.data?.theatres ?? [], [report.data]);
  const totals = utilisationTotals(rows);
  const turnovers = rows.map((r) => r.metrics.averageTurnoverMinutes).filter((m): m is number => m !== null);
  const averageTurnover = turnovers.length ? turnovers.reduce((a, b) => a + b, 0) / turnovers.length : null;

  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 48, right: 16, top: 36, bottom: 32 },
      tooltip: { trigger: 'axis', valueFormatter: (value) => `${String(value)}%` },
      legend: { top: 0 },
      xAxis: { type: 'category', data: rows.map((r) => r.code) },
      yAxis: { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value}%' } },
      series: [
        { type: 'bar', name: 'Utilisation', data: rows.map((r) => r.metrics.utilisationPercent ?? 0) },
        { type: 'bar', name: 'First case on time', data: rows.map((r) => r.metrics.firstCaseOnTimePercent ?? 0) },
      ],
    }),
    [rows],
  );

  return (
    <Section
      title="Theatre utilisation"
      description={`${periodLabel} (${formatDateOnly(from)}${from === to ? '' : ` to ${formatDateOnly(to)}`}) · completed cases only, wheels-in to wheels-out`}
      actions={
        <div className={styles.controls}>
          <Field label="Planned OT time per theatre per day">
            <Select value={String(minutesPerDay)} onChange={(_, d) => setMinutesPerDay(Number(d.value))}>
              <option value="360">6 hours</option>
              <option value="480">8 hours</option>
              <option value="600">10 hours</option>
              <option value="720">12 hours</option>
              <option value="1440">24 hours</option>
            </Select>
          </Field>
        </div>
      }
    >
      {report.error ? (
        <ErrorState error={report.error} onRetry={() => void report.refetch()} />
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard
              label="Utilisation"
              value={report.data ? pct(totals.utilisationPercent) : '…'}
              icon={<DataBarVerticalRegular />}
              hint="Theatre time used of planned time"
            />
            <KpiCard
              label="First case on time"
              value={report.data ? pct(totals.firstCaseOnTimePercent) : '…'}
              icon={<ClockRegular />}
              hint="In the room within 15 min of the slot"
            />
            <KpiCard
              label="Average turnover"
              value={report.data ? formatDuration(averageTurnover) : '…'}
              icon={<TimerRegular />}
              hint="Between consecutive cases"
            />
            <KpiCard
              label="Cases completed"
              value={report.data ? totals.casesCompleted.toLocaleString('en-IN') : '…'}
              icon={<CheckmarkCircleRegular />}
              hint={report.data ? `${totals.casesCancelled} cancelled · ${totals.casesPostponed} postponed` : undefined}
            />
          </div>
          {rows.length > 0 && (
            <EChart option={option} height={220} ariaLabel="Utilisation and on-time first cases by theatre" />
          )}
          <DataTable<OtUtilisationRow>
            ariaLabel="Utilisation by theatre"
            rows={report.data?.theatres}
            loading={report.isLoading}
            getRowId={(r) => r.theatreId}
            size="small"
            columns={[
              {
                key: 'theatre',
                header: 'Theatre',
                render: (r) => <Text weight="semibold">{`${r.code} · ${r.name}`}</Text>,
              },
              { key: 'done', header: 'Completed', align: 'end', render: (r) => r.metrics.casesCompleted },
              {
                key: 'cancelled',
                header: 'Cancelled',
                align: 'end',
                priority: 'low',
                render: (r) => r.metrics.casesCancelled,
              },
              {
                key: 'postponed',
                header: 'Postponed',
                align: 'end',
                priority: 'low',
                render: (r) => r.metrics.casesPostponed,
              },
              {
                key: 'booked',
                header: 'Booked',
                align: 'end',
                priority: 'low',
                render: (r) => formatDuration(r.metrics.bookedMinutes),
              },
              { key: 'actual', header: 'Actual', align: 'end', render: (r) => formatDuration(r.metrics.actualMinutes) },
              { key: 'util', header: 'Utilisation', align: 'end', render: (r) => pct(r.metrics.utilisationPercent) },
              {
                key: 'ontime',
                header: 'First case on time',
                align: 'end',
                render: (r) =>
                  r.metrics.firstCases
                    ? `${pct(r.metrics.firstCaseOnTimePercent)} (${r.metrics.firstCasesOnTime}/${r.metrics.firstCases})`
                    : '—',
              },
              {
                key: 'turnover',
                header: 'Turnover',
                align: 'end',
                priority: 'low',
                render: (r) => formatDuration(r.metrics.averageTurnoverMinutes),
              },
            ]}
            emptyTitle="No theatres to report"
            emptyDescription="Utilisation appears once cases are completed in this period."
          />
          <Text size={200} className={styles.muted}>
            Actual time runs from wheels-in (WHO Sign-in) to wheels-out. Gaps longer than 2 hours are idle time, not
            turnover.
          </Text>
        </>
      )}
    </Section>
  );
}
