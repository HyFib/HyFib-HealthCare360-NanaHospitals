import { Button, Caption1, Field, Select, Text } from '@fluentui/react-components';
import { PrintRegular, TableRegular } from '@fluentui/react-icons';
import { useSession } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, KpiCard, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { monthLabel, MONTH_NAMES, previousMonth, yearOptions } from '@/modules/hr/shared/dates';
import { FilterPad, KpiGrid, Stack, Toolbar } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { qualityQueries } from '@/modules/quality/api';
import { QUALITY_PERMISSIONS as Q } from '@/modules/quality/labels';
import { printScorecard } from '@/modules/quality/print';
import { formatIndicatorValue } from '@/modules/quality/rules';

export const Route = createFileRoute('/_app/quality/indicators/scorecard')({
  validateSearch: (s: Record<string, unknown>): { year?: number; month?: number } => ({
    year: typeof s.year === 'number' && s.year >= 2000 && s.year <= 2100 ? s.year : undefined,
    month: typeof s.month === 'number' && s.month >= 1 && s.month <= 12 ? s.month : undefined,
  }),
  component: () => (
    <RequirePermission permission={Q.indicatorsManage} what="the quality scorecard">
      <ScorecardPage />
    </RequirePermission>
  ),
});

/** Every active indicator for one month (quarterly and yearly ones show the period containing it). */
function ScorecardPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const last = previousMonth();
  const year = search.year ?? last.year;
  const month = search.month ?? last.month;
  const period = `${year}-${String(month).padStart(2, '0')}-01`;
  const scorecard = useQuery(qualityQueries.scorecard(period));
  const rows = scorecard.data ?? [];
  const categories = [...new Set(rows.map((r) => r.category))];
  const met = rows.filter((r) => r.meetsTarget === true).length;
  const missed = rows.filter((r) => r.meetsTarget === false).length;
  const noData = rows.filter((r) => r.value === null).length;
  const title = monthLabel(year, month);

  return (
    <Page>
      <PageHeader
        title="Quality scorecard"
        subtitle={`All active indicators for ${title}, for the quality committee`}
        icon={<TableRegular />}
        breadcrumbs={[{ label: 'Quality indicators', onClick: () => void navigate({ to: '/quality/indicators' }) }, { label: 'Scorecard' }]}
        actions={
          <Button
            appearance="primary"
            icon={<PrintRegular />}
            disabled={rows.length === 0}
            onClick={() => void printScorecard(rows, facilityName, title).catch((error: unknown) => notify.error(error, 'Printing failed'))}
          >
            Print
          </Button>
        }
      />
      <FilterPad>
        <Toolbar spaced={false}>
          <Field label="Month">
            <Select value={String(month)} onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, month: Number(d.value) }) })}>
              {MONTH_NAMES.map((name, index) => (
                <option key={name} value={index + 1}>
                  {name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Year">
            <Select value={String(year)} onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, year: Number(d.value) }) })}>
              {yearOptions(4, 0).map((y) => (
                <option key={y} value={y}>
                  {y}
                </option>
              ))}
            </Select>
          </Field>
        </Toolbar>
      </FilterPad>
      {scorecard.isLoading && <LoadingState />}
      {scorecard.error && <ErrorState error={scorecard.error} onRetry={() => void scorecard.refetch()} />}
      {scorecard.data && rows.length === 0 && (
        <Section>
          <EmptyState title="No active indicators" description="Add indicators on the Quality indicators screen." />
        </Section>
      )}
      {rows.length > 0 && (
        <Stack gap={16}>
          <KpiGrid>
            <KpiCard label="On target" value={met} />
            <KpiCard label="Off target" value={missed} />
            <KpiCard label="Not recorded" value={noData} hint={noData > 0 ? 'Record the missing values' : undefined} />
          </KpiGrid>
          {categories.map((category) => (
            <Section key={category} title={category} flush>
              <DataTable
                ariaLabel={`${category} scorecard`}
                rows={rows.filter((r) => r.category === category)}
                getRowId={(r) => r.indicatorId}
                onRowClick={(r) => void navigate({ to: '/quality/indicators/$indicatorId', params: { indicatorId: r.indicatorId } })}
                columns={[
                  {
                    key: 'name',
                    header: 'Indicator',
                    render: (r) => (
                      <span>
                        <Text weight="semibold">{r.name}</Text> <Caption1>{r.code}</Caption1>
                      </span>
                    ),
                  },
                  { key: 'period', header: 'Period', width: 120, render: (r) => r.periodLabel },
                  { key: 'value', header: 'Value', align: 'end', width: 140, render: (r) => formatIndicatorValue(r.value, r.unit) },
                  {
                    key: 'target',
                    header: 'Target',
                    align: 'end',
                    width: 150,
                    render: (r) => (r.target === null ? '—' : `${r.direction === 'HigherIsBetter' ? '≥' : '≤'} ${formatIndicatorValue(r.target, r.unit)}`),
                  },
                  {
                    key: 'status',
                    header: 'Status',
                    width: 130,
                    render: (r) =>
                      r.value === null ? (
                        <StatusBadge status="Pending" tone="warning" label="Not recorded" />
                      ) : r.meetsTarget === null ? (
                        <StatusBadge status="None" tone="informative" label="No target" />
                      ) : (
                        <StatusBadge status={r.meetsTarget ? 'Met' : 'Missed'} tone={r.meetsTarget ? 'success' : 'danger'} label={r.meetsTarget ? 'On target' : 'Off target'} />
                      ),
                  },
                ]}
                size="small"
              />
            </Section>
          ))}
        </Stack>
      )}
    </Page>
  );
}
