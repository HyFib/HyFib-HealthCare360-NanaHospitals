import { Button, Caption1, Field, Select, Text, Tooltip } from '@fluentui/react-components';
import { DataTrendingRegular, DeleteRegular, EditRegular, NotepadRegular } from '@fluentui/react-icons';
import { humanize, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, EmptyState, ErrorState, KpiCard, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { addMonths } from '@/modules/hr/shared/dates';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { KpiGrid } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { qualityApi, qualityKeys, qualityQueries, type IndicatorDto, type IndicatorValueDto } from '@/modules/quality/api';
import { IndicatorDrawer } from '@/modules/quality/IndicatorDrawer';
import { IndicatorValueDialog } from '@/modules/quality/IndicatorValueDialog';
import { indicatorUnitLabel, QUALITY_PERMISSIONS as Q } from '@/modules/quality/labels';
import { formatIndicatorValue, trendText, trendTone } from '@/modules/quality/rules';
import { TrendChart } from '@/modules/quality/TrendChart';

type Range = '12p' | '24m' | '36m';

export const Route = createFileRoute('/_app/quality/indicators/$indicatorId')({
  validateSearch: (s: Record<string, unknown>): { range?: Range } => ({
    range: s.range === '24m' || s.range === '36m' ? s.range : undefined,
  }),
  component: () => (
    <RequirePermission permission={Q.indicatorsManage} what="quality indicators">
      <IndicatorPage />
    </RequirePermission>
  ),
});

function IndicatorPage() {
  const { indicatorId } = Route.useParams();
  // There is no single-indicator endpoint; the list is small and cached.
  const indicators = useQuery(qualityQueries.indicators(true));
  if (indicators.isLoading) {
    return <LoadingState />;
  }
  const indicator = indicators.data?.find((i) => i.id === indicatorId);
  if (indicators.error || !indicator) {
    return (
      <Page maxWidth={900}>
        {indicators.error ? (
          <ErrorState error={indicators.error} onRetry={() => void indicators.refetch()} />
        ) : (
          <Section>
            <EmptyState title="Indicator not found" description="It may have been removed. Go back to the list of indicators." />
          </Section>
        )}
      </Page>
    );
  }
  return <IndicatorView indicator={indicator} categories={[...new Set((indicators.data ?? []).map((i) => i.category))]} />;
}

function IndicatorView({ indicator: ind, categories }: { indicator: IndicatorDto; categories: string[] }) {
  const navigate = useNavigate({ from: Route.fullPath });
  const { range = '12p' } = Route.useSearch();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [recording, setRecording] = useState<IndicatorValueDto | 'new' | null>(null);
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState<IndicatorValueDto | null>(null);
  const today = todayIso();
  const from = range === '12p' ? undefined : addMonths(today, range === '24m' ? -24 : -36);
  const trend = useQuery(qualityQueries.indicatorTrend(ind.id, from, from ? today : undefined));
  const values = useQuery(qualityQueries.indicatorValues(ind.id, from ?? addMonths(today, -36), today));

  const remove = useMutation({
    mutationFn: (id: string) => qualityApi.deleteValue(id),
    onSuccess: () => {
      setDeleting(null);
      notify.success('Value removed');
      void queryClient.invalidateQueries({ queryKey: qualityKeys.indicators });
    },
    onError: (error) => notify.error(error, 'Could not remove the value'),
  });

  const t = trend.data;
  const sortedValues = [...(values.data ?? [])].sort((a, b) => b.periodStart.localeCompare(a.periodStart));

  return (
    <Page>
      <PageHeader
        title={ind.name}
        subtitle={`${ind.code}${ind.nabhReference ? ` · NABH ${ind.nabhReference}` : ''} · ${ind.category} · measured ${humanize(ind.frequency).toLowerCase()}`}
        icon={<DataTrendingRegular />}
        breadcrumbs={[{ label: 'Quality indicators', onClick: () => void navigate({ to: '/quality/indicators' }) }, { label: ind.code }]}
        actions={
          <>
            <Button icon={<EditRegular />} onClick={() => setEditing(true)}>
              Edit
            </Button>
            <Button appearance="primary" icon={<NotepadRegular />} onClick={() => setRecording('new')}>
              Record value
            </Button>
          </>
        }
      />
      <KpiGrid>
        <KpiCard label="Latest" value={formatIndicatorValue(t?.latest ?? ind.latest?.value ?? null, ind.unit)} hint={ind.latest?.periodLabel} />
        <KpiCard label="Target" value={ind.target === null ? 'None' : formatIndicatorValue(ind.target, ind.unit)} hint={ind.direction === 'HigherIsBetter' ? 'Higher is better' : 'Lower is better'} />
        <KpiCard label="Average" value={formatIndicatorValue(t?.average ?? null, ind.unit)} hint="Over the periods shown" />
        <KpiCard label="Trend" value={t ? <StatusBadge status={t.trend} tone={trendTone(t.trend)} label={trendText(t.trend)} /> : '…'} hint="Latest against the previous three" />
      </KpiGrid>
      <Section
        title="Trend"
        description="Green points meet the target, red points miss it; the dashed line is the target."
        actions={
          <Field label="Show" orientation="horizontal">
            <Select value={range} onChange={(_, d) => void navigate({ search: { range: d.value === '12p' ? undefined : (d.value as Range) }, replace: true })}>
              <option value="12p">Last 12 periods</option>
              <option value="24m">Last 2 years</option>
              <option value="36m">Last 3 years</option>
            </Select>
          </Field>
        }
      >
        {trend.isLoading && <LoadingState />}
        {trend.error && <ErrorState error={trend.error} onRetry={() => void trend.refetch()} />}
        {t && t.points.length === 0 && <EmptyState title="No values yet" description='Use "Record value" to enter the first period.' />}
        {t && t.points.length > 0 && <TrendChart points={t.points} unit={ind.unit} target={ind.target} direction={ind.direction} name={ind.name} />}
      </Section>
      <Section title="Definition">
        <DetailGrid>
          <Detail label="Numerator" wide>
            {ind.numeratorDefinition}
          </Detail>
          <Detail label="Denominator" wide>
            {ind.denominatorDefinition}
          </Detail>
          <Detail label="Unit">{indicatorUnitLabel[ind.unit]}</Detail>
          <Detail label="Multiplier">{ind.multiplier.toLocaleString('en-IN')}</Detail>
          <Detail label="Status">{ind.isActive ? 'In use' : 'No longer used'}</Detail>
        </DetailGrid>
      </Section>
      <Section title="Recorded values" flush>
        <DataTable
          ariaLabel="Recorded values"
          rows={values.isLoading ? undefined : sortedValues}
          loading={values.isLoading}
          error={values.error}
          onRetry={() => void values.refetch()}
          getRowId={(v) => v.id}
          columns={[
            { key: 'period', header: 'Period', render: (v) => <Text weight="semibold">{v.periodLabel}</Text> },
            { key: 'num', header: 'Numerator', align: 'end', render: (v) => v.numerator.toLocaleString('en-IN') },
            { key: 'den', header: 'Denominator', align: 'end', render: (v) => v.denominator.toLocaleString('en-IN') },
            { key: 'value', header: 'Value', align: 'end', render: (v) => formatIndicatorValue(v.value, ind.unit) },
            {
              key: 'status',
              header: 'Target',
              render: (v) =>
                v.meetsTarget === null ? '—' : <StatusBadge status={v.meetsTarget ? 'Met' : 'Missed'} tone={v.meetsTarget ? 'success' : 'danger'} label={v.meetsTarget ? 'Met' : 'Missed'} />,
            },
            { key: 'notes', header: 'Notes', priority: 'low', render: (v) => (v.notes ? <Caption1>{v.notes}</Caption1> : '—') },
          ]}
          rowActions={(v) => (
            <span style={{ display: 'inline-flex', gap: 4 }}>
              <Tooltip content="Correct this value" relationship="label">
                <Button size="small" appearance="subtle" icon={<EditRegular />} onClick={() => setRecording(v)} />
              </Tooltip>
              <Tooltip content="Remove this value" relationship="label">
                <Button size="small" appearance="subtle" icon={<DeleteRegular />} onClick={() => setDeleting(v)} />
              </Tooltip>
            </span>
          )}
          emptyTitle="No values recorded"
          size="small"
        />
      </Section>
      {recording && <IndicatorValueDialog open onClose={() => setRecording(null)} indicator={ind} value={recording === 'new' ? null : recording} />}
      {editing && <IndicatorDrawer indicator={ind} categories={categories} onClose={() => setEditing(false)} />}
      <ConfirmDialog
        open={deleting !== null}
        title="Remove this value?"
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting.id)}
        onCancel={() => setDeleting(null)}
      >
        {deleting ? `${deleting.periodLabel}: ${formatIndicatorValue(deleting.value, ind.unit)}. The scorecard for that period will show no data.` : null}
      </ConfirmDialog>
    </Page>
  );
}
