import { Badge, Button, Caption1 } from '@fluentui/react-components';
import { CalendarRegular, EditRegular, PeopleRegular, StethoscopeRegular } from '@fluentui/react-icons';
import { formatDateTime, formatPhone, humanize, todayIso } from '@hc360/core';
import { ErrorState, FilterBar, KpiCard, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { crmQueries, type ReferralStats, type ReferringDoctorDetail } from '@/modules/crm/api';
import { CRM_PERMISSIONS as C } from '@/modules/crm/labels';
import { ReferringDoctorDrawer } from '@/modules/crm/ReferringDoctorDrawer';
import { PeriodFields } from '@/modules/feedback/PeriodFields';
import { PERIOD_PRESETS, resolvePeriod, type PeriodPreset } from '@/modules/feedback/rules';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { isIsoDate, monthKeyLabel } from '@/modules/hr/shared/dates';
import { Columns, FilterPad, KpiGrid, MainAside, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

interface DoctorSearch {
  preset?: PeriodPreset;
  from?: string;
  to?: string;
}

export const Route = createFileRoute('/_app/crm/referrals/$doctorId')({
  validateSearch: (s: Record<string, unknown>): DoctorSearch => ({
    preset: PERIOD_PRESETS.includes(s.preset as PeriodPreset) ? (s.preset as PeriodPreset) : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
  }),
  component: () => (
    <RequirePermission permission={C.referralsManage} what="referring doctors">
      <DoctorPage />
    </RequirePermission>
  ),
});

// Canvas charts cannot read CSS variables, so chart colours are fixed (readable on light and dark themes).
const BLUE = '#0A7CA7';
const PALETTE = ['#0A7CA7', '#5C2E91', '#CA5010', '#038387', '#8764B8', '#498205', '#C239B3', '#986F0B'];

function DoctorPage() {
  const { doctorId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [editing, setEditing] = useState(false);
  const preset = search.preset ?? 'last12Months';
  const period = resolvePeriod(preset, search.from, search.to, todayIso());
  const doctor = useQuery(crmQueries.referringDoctor(doctorId));
  const stats = useQuery(crmQueries.referralStats(doctorId, period.from, period.to));
  const crumbs = [
    { label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) },
    { label: 'Referring doctors', onClick: () => void navigate({ to: '/crm/referrals' }) },
  ];

  if (!doctor.data) {
    return (
      <Page>
        <PageHeader title="Referring doctor" icon={<StethoscopeRegular />} breadcrumbs={crumbs} />
        {doctor.error ? <ErrorState error={doctor.error} onRetry={() => void doctor.refetch()} /> : <LoadingState />}
      </Page>
    );
  }

  const d = doctor.data;
  return (
    <Page>
      <PageHeader
        title={
          <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            {d.name}
            {!d.isActive && (
              <Badge appearance="tint" color="subtle">
                Inactive
              </Badge>
            )}
          </span>
        }
        subtitle={[d.specialty, d.clinic, d.city].filter(Boolean).join(' · ') || undefined}
        icon={<StethoscopeRegular />}
        breadcrumbs={crumbs}
        actions={
          <Button icon={<EditRegular />} onClick={() => setEditing(true)}>
            Edit
          </Button>
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <PeriodFields
              preset={preset}
              from={period.from}
              to={period.to}
              onChange={(patch) => void navigate({ search: (prev) => ({ ...prev, ...patch }) })}
            />
          </FilterBar>
        </FilterPad>
      </Section>
      <MainAside>
        {stats.error ? (
          <ErrorState error={stats.error} onRetry={() => void stats.refetch()} />
        ) : !stats.data ? (
          <LoadingState label="Counting referrals…" />
        ) : (
          <StatsView stats={stats.data} />
        )}
        <DoctorDetails doctor={d} />
      </MainAside>
      {editing && <ReferringDoctorDrawer doctorId={d.id} onClose={() => setEditing(false)} />}
    </Page>
  );
}

function StatsView({ stats }: { stats: ReferralStats }) {
  const byMonth = useMemo<ChartOption>(
    () => ({
      grid: { left: 40, right: 16, top: 16, bottom: 32 },
      tooltip: { trigger: 'axis' },
      xAxis: {
        type: 'category',
        data: stats.byMonth.map((m) => monthKeyLabel(m.label)),
        axisLabel: { hideOverlap: true },
      },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        {
          type: 'bar',
          name: 'Referrals',
          data: stats.byMonth.map((m) => m.count),
          itemStyle: { color: BLUE },
          barMaxWidth: 32,
        },
      ],
    }),
    [stats.byMonth],
  );
  const byDepartment = useMemo<ChartOption>(() => {
    const rows = [...stats.byDepartment].slice(0, 10).reverse();
    return {
      grid: { left: 150, right: 32, top: 8, bottom: 24 },
      tooltip: { trigger: 'axis' },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: { type: 'category', data: rows.map((r) => r.label) },
      series: [
        {
          type: 'bar',
          name: 'Referrals',
          data: rows.map((r) => r.count),
          itemStyle: { color: '#5C2E91' },
          barMaxWidth: 22,
          label: { show: true, position: 'right' },
        },
      ],
    };
  }, [stats.byDepartment]);
  const byType = useMemo<ChartOption>(
    () => ({
      tooltip: { trigger: 'item', formatter: '{b}: {c} ({d}%)' },
      legend: { bottom: 0 },
      series: [
        {
          type: 'pie',
          name: 'Visit type',
          radius: ['45%', '72%'],
          top: -10,
          label: { formatter: '{d}%' },
          data: stats.byVisitType.map((t, index) => ({
            name: humanize(t.label),
            value: t.count,
            itemStyle: { color: PALETTE[index % PALETTE.length] },
          })),
        },
      ],
    }),
    [stats.byVisitType],
  );

  return (
    <Stack>
      <KpiGrid>
        <KpiCard
          label="Referrals in the period"
          value={stats.referrals.toLocaleString('en-IN')}
          icon={<PeopleRegular />}
        />
        <KpiCard
          label="Last referral"
          value={stats.lastReferralAt ? formatDateTime(stats.lastReferralAt) : '—'}
          icon={<CalendarRegular />}
        />
      </KpiGrid>
      <Section title="Month by month">
        {stats.referrals === 0 ? (
          <Caption1>No referrals in this period.</Caption1>
        ) : (
          <EChart option={byMonth} height={240} ariaLabel="Referrals per month" />
        )}
      </Section>
      {stats.referrals > 0 && (
        <Columns>
          <Section title="Departments">
            <EChart
              option={byDepartment}
              height={Math.max(140, Math.min(stats.byDepartment.length, 10) * 34 + 40)}
              ariaLabel="Referrals by department"
            />
          </Section>
          <Section title="Kind of visit">
            <EChart option={byType} height={240} ariaLabel="Referrals by kind of visit" />
          </Section>
        </Columns>
      )}
    </Stack>
  );
}

function DoctorDetails({ doctor: d }: { doctor: ReferringDoctorDetail }) {
  return (
    <Section title="Details">
      <DetailGrid minColumnWidth={160}>
        <Detail label="Phone">{d.phone ? formatPhone(d.phone) : null}</Detail>
        <Detail label="Email">{d.email}</Detail>
        <Detail label="Medical council registration" mono>
          {d.registrationNumber}
        </Detail>
        <Detail label="Area">{d.city}</Detail>
        <Detail label="Other spellings" wide>
          {d.alternateNames.length > 0 ? (
            <span style={{ display: 'inline-flex', gap: 6, flexWrap: 'wrap' }}>
              {d.alternateNames.map((n) => (
                <Badge key={n} appearance="tint" shape="rounded">
                  {n}
                </Badge>
              ))}
            </span>
          ) : null}
        </Detail>
        {d.notes && (
          <Detail label="Notes" wide>
            <span style={{ whiteSpace: 'pre-wrap' }}>{d.notes}</span>
          </Detail>
        )}
      </DetailGrid>
    </Section>
  );
}
