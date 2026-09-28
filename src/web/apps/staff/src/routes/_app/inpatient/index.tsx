import { Button, Caption1, Field, Input, makeStyles, Select, Text, ToggleButton, tokens } from '@fluentui/react-components';
import {
  ArrowEnterRegular,
  ArrowExitRegular,
  BedRegular,
  BroomRegular,
  CalendarLtrRegular,
  ChartMultipleRegular,
  PersonAddRegular,
  TimerRegular,
} from '@fluentui/react-icons';
import { formatDate, formatDateOnly, todayIso } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { inpatientQueries, WARD_TYPE_LABEL, type CensusReport, type WardCensus } from '@/modules/inpatient/api';
import { CENSUS_PRESETS, clampRange, matchPreset, presetRange, shortDate, sumTransfers } from '@/modules/inpatient/census';
import { useInpatientPermissions, useInpatientRealtime } from '@/modules/inpatient/hooks';
import { formatDuration } from '@/modules/inpatient/time';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface CensusSearch {
  from?: string;
  to?: string;
  wardId?: string;
}

export const Route = createFileRoute('/_app/inpatient/')({
  validateSearch: (s: Record<string, unknown>): CensusSearch => ({
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
  }),
  component: CensusPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))' },
  columns: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 3fr) minmax(0, 2fr)',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  presets: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'flex-end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** In-patient census (WB-06): live occupancy, movements for a period, ALOS, bed occupancy rate and pending discharges. */
function CensusPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const permissions = useInpatientPermissions();
  useInpatientRealtime();

  const today = todayIso();
  const range = clampRange(search.from ?? presetRange('7d', today).from, search.to ?? today, today);
  const preset = matchPreset(range.from, range.to, today);
  const wardId = search.wardId ?? null;

  const wards = useQuery(inpatientQueries.wards());
  const census = useQuery(inpatientQueries.census(range.from, range.to, wardId));
  const occupancy = useQuery(inpatientQueries.occupancy());
  const pending = useQuery(inpatientQueries.pendingDischarges(wardId));

  const setRange = (from: string, to: string) => {
    const next = clampRange(from, to, today);
    void navigate({ search: (prev) => ({ ...prev, from: next.from, to: next.to }), replace: true });
  };

  const live = wardId ? occupancy.data?.wards.find((w) => w.wardId === wardId)?.counts : occupancy.data?.total;
  const c = census.data;
  const summary = c ? (wardId ? (c.wards.find((w) => w.wardId === wardId)?.summary ?? c.hospital) : c.hospital) : undefined;
  const value = (n: number | undefined) => (n === undefined ? '…' : n.toLocaleString('en-IN'));

  return (
    <Page>
      <PageHeader
        title="In-patient census"
        icon={<ChartMultipleRegular />}
        subtitle={`${formatDateOnly(range.from)} to ${formatDateOnly(range.to)} · figures by IST calendar day`}
        actions={
          <>
            <Button icon={<BedRegular />} onClick={() => void navigate({ to: '/inpatient/beds', search: wardId ? { wardId } : {} })}>
              Bed board
            </Button>
            {permissions.admit && (
              <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => void navigate({ to: '/inpatient/admit' })}>
                Admit patient
              </Button>
            )}
          </>
        }
      />

      <Section>
        <FilterBar>
          <div className={styles.presets} role="group" aria-label="Period">
            {CENSUS_PRESETS.map((p) => (
              <ToggleButton
                key={p.value}
                checked={preset === p.value}
                onClick={() => {
                  const r = presetRange(p.value, today);
                  setRange(r.from, r.to);
                }}
              >
                {p.label}
              </ToggleButton>
            ))}
          </div>
          <Field label="From">
            <Input type="date" value={range.from} max={range.to} onChange={(_, d) => d.value && setRange(d.value, range.to)} />
          </Field>
          <Field label="To">
            <Input type="date" value={range.to} min={range.from} max={today} onChange={(_, d) => d.value && setRange(range.from, d.value)} />
          </Field>
          <Field label="Ward">
            <Select value={wardId ?? ''} onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, wardId: d.value || undefined }), replace: true })}>
              <option value="">Whole hospital</option>
              {wards.data?.map((w) => (
                <option key={w.id} value={w.id}>
                  {w.name}
                </option>
              ))}
            </Select>
          </Field>
        </FilterBar>
      </Section>

      <div className={styles.kpis}>
        <KpiCard
          label="Occupied now"
          icon={<BedRegular />}
          value={live ? `${live.occupancyPercent}%` : '…'}
          hint={live ? `${live.occupied} of ${live.operational} beds in service` : 'Live'}
          onClick={() => void navigate({ to: '/inpatient/beds', search: wardId ? { wardId, status: 'Occupied' } : { status: 'Occupied' } })}
        />
        <KpiCard
          label="Vacant now"
          icon={<BedRegular />}
          value={value(live?.vacant)}
          hint={live ? `${live.cleaning} being cleaned · ${live.reserved} reserved` : 'Live'}
          onClick={() => void navigate({ to: '/inpatient/beds', search: wardId ? { wardId, status: 'Vacant' } : { status: 'Vacant' } })}
        />
        <KpiCard label="Admissions" icon={<ArrowEnterRegular />} value={value(summary?.admissions)} hint="In the period" />
        <KpiCard
          label="Discharges"
          icon={<ArrowExitRegular />}
          value={value(summary?.discharges)}
          hint={summary ? (summary.deaths ? `Including ${summary.deaths} deaths` : 'No deaths') : 'In the period'}
        />
        <KpiCard
          label="Pending discharges"
          icon={<TimerRegular />}
          value={value(pending.data?.length)}
          hint="Waiting for billing clearance"
          onClick={() => document.getElementById('pending-discharges')?.scrollIntoView({ behavior: 'smooth' })}
        />
        <KpiCard label="Average stay" icon={<CalendarLtrRegular />} value={summary ? `${summary.averageLengthOfStay} d` : '…'} hint="Of patients discharged" />
        <KpiCard
          label="Bed occupancy rate"
          icon={<ChartMultipleRegular />}
          value={summary ? `${summary.bedOccupancyRate}%` : '…'}
          hint={summary ? `${summary.inpatientDays.toLocaleString('en-IN')} in-patient days` : 'Midnight census'}
        />
        {!wardId && (
          <KpiCard
            label="Bed turnaround"
            icon={<BroomRegular />}
            value={c?.averageHousekeepingTurnaroundMinutes != null ? formatDuration(c.averageHousekeepingTurnaroundMinutes) : '—'}
            hint={c ? `${c.housekeepingTasksCompleted} beds cleaned` : 'Discharge to clean bed'}
            onClick={permissions.housekeeping || permissions.manageBeds ? () => void navigate({ to: '/inpatient/housekeeping' }) : undefined}
          />
        )}
      </div>

      {census.error && !census.data ? (
        <ErrorState error={census.error} onRetry={() => void census.refetch()} title="Could not load the census" />
      ) : (
        <div className={styles.columns}>
          <Section title="Daily movement" description="Admissions and discharges each day, with the patients in bed at midnight">
            {summary ? (
              summary.days.length > 0 ? (
                <CensusChart days={summary.days} />
              ) : (
                <Text className={styles.muted}>No days in this period.</Text>
              )
            ) : (
              <Caption1 className={styles.muted}>Loading…</Caption1>
            )}
          </Section>
          <PendingDischarges wardId={wardId} />
        </div>
      )}

      {!wardId && <WardTable report={census.data} loading={census.isLoading} liveWards={occupancy.data?.wards} />}
    </Page>
  );
}

function CensusChart({ days }: { days: CensusReport['hospital']['days'] }) {
  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 40, right: 16, top: 36, bottom: 32 },
      tooltip: { trigger: 'axis' },
      legend: { top: 0 },
      xAxis: { type: 'category', data: days.map((d) => shortDate(d.date)) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        { type: 'bar', name: 'Admissions', data: days.map((d) => d.admissions), itemStyle: { color: '#0A7CA7' } },
        { type: 'bar', name: 'Discharges', data: days.map((d) => d.discharges), itemStyle: { color: '#8764B8' } },
        { type: 'line', name: 'In bed at midnight', data: days.map((d) => d.midnightCensus), smooth: true, symbolSize: 6, itemStyle: { color: '#C19C00' } },
      ],
    }),
    [days],
  );
  const total = days.reduce((sum, d) => sum + d.admissions, 0);
  return (
    <EChart
      option={option}
      height={280}
      ariaLabel={`Daily admissions and discharges from ${days[0]?.date ?? ''} to ${days.at(-1)?.date ?? ''}: ${total} admissions in total`}
    />
  );
}

function PendingDischarges({ wardId }: { wardId: string | null }) {
  const navigate = useNavigate();
  const pending = useQuery(inpatientQueries.pendingDischarges(wardId));
  return (
    <div id="pending-discharges">
      <Section title="Pending discharges" description="Oldest first. Ask billing to clear the ones waiting longest." flush>
        <DataTable
          ariaLabel="Pending discharges"
          rows={pending.data}
          loading={pending.isLoading}
          error={pending.error}
          onRetry={() => void pending.refetch()}
          getRowId={(p) => p.admissionId}
          onRowClick={(p) => void navigate({ to: '/inpatient/admissions/$admissionId', params: { admissionId: p.admissionId } })}
          size="small"
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (p) => (
                <span>
                  <Text weight="semibold">{p.patientName}</Text>
                  <Caption1 block>{[p.wardName, p.bedLabel].filter(Boolean).join(' · ')}</Caption1>
                </span>
              ),
            },
            { key: 'expected', header: 'Expected', width: 110, priority: 'low', render: (p) => (p.expectedDischargeDate ? formatDateOnly(p.expectedDischargeDate) : '—') },
            {
              key: 'wait',
              header: 'Waiting',
              width: 100,
              render: (p) => (
                <Text weight={p.waitingMinutes >= 180 ? 'semibold' : 'regular'} style={p.waitingMinutes >= 180 ? { color: tokens.colorPaletteRedForeground1 } : undefined}>
                  {formatDuration(p.waitingMinutes)}
                </Text>
              ),
            },
          ]}
          emptyTitle="No discharges in progress"
          emptyDescription="Discharges started by doctors appear here until billing clears them."
        />
      </Section>
    </div>
  );
}

function WardTable({ report, loading, liveWards }: { report: CensusReport | undefined; loading: boolean; liveWards: { wardId: string; counts: { occupancyPercent: number; occupied: number; operational: number } }[] | undefined }) {
  const navigate = useNavigate();
  const live = new Map((liveWards ?? []).map((w) => [w.wardId, w.counts]));
  return (
    <Section title="By ward" description={report ? `${formatDate(new Date().toISOString())} occupancy and period movements` : undefined} flush>
      <DataTable<WardCensus>
        ariaLabel="Census by ward"
        rows={report?.wards}
        loading={loading}
        getRowId={(w) => w.wardId}
        onRowClick={(w) => void navigate({ to: '/inpatient', search: (prev) => ({ ...prev, wardId: w.wardId }) })}
        size="small"
        columns={[
          {
            key: 'ward',
            header: 'Ward',
            render: (w) => (
              <span>
                <Text weight="semibold">{w.name}</Text> <Caption1>{WARD_TYPE_LABEL[w.type] ?? w.type}</Caption1>
              </span>
            ),
          },
          {
            key: 'now',
            header: 'Occupied now',
            width: 130,
            render: (w) => {
              const counts = live.get(w.wardId);
              return counts ? `${counts.occupied}/${counts.operational} (${counts.occupancyPercent}%)` : '—';
            },
          },
          { key: 'adm', header: 'Admissions', width: 100, align: 'end', render: (w) => w.summary.admissions },
          { key: 'dis', header: 'Discharges', width: 100, align: 'end', render: (w) => w.summary.discharges },
          {
            key: 'transfers',
            header: 'Transfers in / out',
            width: 140,
            priority: 'low',
            render: (w) => {
              const t = sumTransfers(w.summary.days);
              return `${t.transfersIn} / ${t.transfersOut}`;
            },
          },
          { key: 'days', header: 'IP days', width: 90, align: 'end', priority: 'low', render: (w) => w.summary.inpatientDays },
          { key: 'alos', header: 'Avg stay', width: 90, align: 'end', priority: 'low', render: (w) => `${w.summary.averageLengthOfStay} d` },
          { key: 'bor', header: 'Occupancy rate', width: 120, align: 'end', render: (w) => `${w.summary.bedOccupancyRate}%` },
        ]}
        emptyTitle="No wards"
        emptyDescription="Set up wards and beds to see the census."
      />
    </Section>
  );
}
