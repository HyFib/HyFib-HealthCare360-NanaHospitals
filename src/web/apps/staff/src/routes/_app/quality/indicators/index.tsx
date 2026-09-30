import { Button, Caption1, Checkbox, Field, Select, Text, Tooltip } from '@fluentui/react-components';
import { AddRegular, DataTrendingRegular, EditRegular, NotepadRegular, TableRegular } from '@fluentui/react-icons';
import { humanize } from '@hc360/core';
import {
  DataTable,
  EmptyState,
  ErrorState,
  FilterBar,
  LoadingState,
  Page,
  PageHeader,
  Section,
  StatusBadge,
} from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { FilterPad, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { qualityQueries, type IndicatorDto } from '@/modules/quality/api';
import { IndicatorDrawer } from '@/modules/quality/IndicatorDrawer';
import { IndicatorValueDialog } from '@/modules/quality/IndicatorValueDialog';
import { QUALITY_PERMISSIONS as Q } from '@/modules/quality/labels';
import { formatIndicatorValue } from '@/modules/quality/rules';

export const Route = createFileRoute('/_app/quality/indicators/')({
  validateSearch: (s: Record<string, unknown>): { category?: string; inactive?: boolean } => ({
    category: typeof s.category === 'string' && s.category ? s.category : undefined,
    inactive: s.inactive === true ? true : undefined,
  }),
  component: () => (
    <RequirePermission permission={Q.indicatorsManage} what="quality indicators">
      <IndicatorsPage />
    </RequirePermission>
  ),
});

function IndicatorsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [editing, setEditing] = useState<IndicatorDto | 'new' | null>(null);
  const [recording, setRecording] = useState<IndicatorDto | null>(null);
  const indicators = useQuery(qualityQueries.indicators(Boolean(search.inactive)));
  const categories = useMemo(
    () => [...new Set((indicators.data ?? []).map((i) => i.category))].sort(),
    [indicators.data],
  );
  const shown = (indicators.data ?? []).filter((i) => !search.category || i.category === search.category);
  const groups = categories
    .filter((c) => !search.category || c === search.category)
    .map((category) => ({ category, rows: shown.filter((i) => i.category === category) }));
  const met = shown.filter((i) => i.latest?.meetsTarget === true).length;
  const missed = shown.filter((i) => i.latest?.meetsTarget === false).length;

  return (
    <Page>
      <PageHeader
        title="Quality indicators"
        subtitle={
          indicators.data
            ? `${shown.length} indicators · latest values: ${met} on target, ${missed} off target`
            : 'NABH quality indicators with targets'
        }
        icon={<DataTrendingRegular />}
        actions={
          <>
            <Button icon={<TableRegular />} onClick={() => void navigate({ to: '/quality/indicators/scorecard' })}>
              Scorecard
            </Button>
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add indicator
            </Button>
          </>
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Category">
              <Select
                value={search.category ?? ''}
                onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, category: d.value || undefined }) })}
              >
                <option value="">All</option>
                {categories.map((c) => (
                  <option key={c} value={c}>
                    {c}
                  </option>
                ))}
              </Select>
            </Field>
            <Checkbox
              style={{ alignSelf: 'flex-end' }}
              checked={Boolean(search.inactive)}
              onChange={(_, d) =>
                void navigate({ search: (prev) => ({ ...prev, inactive: d.checked ? true : undefined }) })
              }
              label="Include indicators no longer used"
            />
          </FilterBar>
        </FilterPad>
        {indicators.isLoading && <LoadingState />}
        {indicators.error && (
          <div style={{ padding: '0 16px 16px' }}>
            <ErrorState error={indicators.error} onRetry={() => void indicators.refetch()} />
          </div>
        )}
        {indicators.data && shown.length === 0 && (
          <EmptyState
            icon={<DataTrendingRegular />}
            title="No indicators"
            description="Add the indicators the quality committee tracks, with their definitions and targets."
          />
        )}
      </Section>
      <Stack gap={16}>
        {groups.map((group) => (
          <Section key={group.category} title={group.category} flush>
            <DataTable
              ariaLabel={`${group.category} indicators`}
              rows={group.rows}
              getRowId={(i) => i.id}
              onRowClick={(i) =>
                void navigate({ to: '/quality/indicators/$indicatorId', params: { indicatorId: i.id } })
              }
              columns={[
                {
                  key: 'name',
                  header: 'Indicator',
                  render: (i) => (
                    <span>
                      <Text weight="semibold">{i.name}</Text>
                      <Caption1 block>
                        {i.code}
                        {i.nabhReference ? ` · NABH ${i.nabhReference}` : ''} · {humanize(i.frequency)}
                        {i.isActive ? '' : ' · not in use'}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'target',
                  header: 'Target',
                  width: 170,
                  render: (i) =>
                    i.target === null
                      ? '—'
                      : `${i.direction === 'HigherIsBetter' ? '≥' : '≤'} ${formatIndicatorValue(i.target, i.unit)}`,
                },
                {
                  key: 'latest',
                  header: 'Latest',
                  render: (i) =>
                    i.latest ? (
                      <span>
                        <Text weight="semibold">{formatIndicatorValue(i.latest.value, i.unit)}</Text>{' '}
                        <Caption1>{i.latest.periodLabel}</Caption1>
                      </span>
                    ) : (
                      <Caption1>No value yet</Caption1>
                    ),
                },
                {
                  key: 'status',
                  header: 'Status',
                  width: 130,
                  render: (i) =>
                    i.latest?.meetsTarget === true ? (
                      <StatusBadge status="Met" tone="success" label="On target" />
                    ) : i.latest?.meetsTarget === false ? (
                      <StatusBadge status="Missed" tone="danger" label="Off target" />
                    ) : (
                      <StatusBadge status="None" tone="informative" label={i.latest ? 'No target' : 'No data'} />
                    ),
                },
              ]}
              rowActions={(i) => (
                <span style={{ display: 'inline-flex', gap: 4 }}>
                  <Tooltip content="Record this period's value" relationship="label">
                    <Button size="small" icon={<NotepadRegular />} onClick={() => setRecording(i)}>
                      Record
                    </Button>
                  </Tooltip>
                  <Tooltip content="Edit definition and target" relationship="label">
                    <Button size="small" appearance="subtle" icon={<EditRegular />} onClick={() => setEditing(i)} />
                  </Tooltip>
                </span>
              )}
              size="small"
            />
          </Section>
        ))}
      </Stack>
      {editing && (
        <IndicatorDrawer
          indicator={editing === 'new' ? null : editing}
          categories={categories}
          onClose={() => setEditing(null)}
        />
      )}
      {recording && <IndicatorValueDialog open onClose={() => setRecording(null)} indicator={recording} />}
    </Page>
  );
}
