import {
  Button,
  Caption1,
  makeStyles,
  mergeClasses,
  Subtitle2,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  BoardRegular,
  ChevronRightRegular,
  DataTrendingRegular,
  DocumentTableRegular,
} from '@fluentui/react-icons';
import { formatRelative, todayIso } from '@hc360/core';
import { EmptyState, ErrorState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { EChart } from '@/lib/EChart';
import {
  analyticsKeys,
  analyticsQueries,
  type KpiValue,
  type ReportInputs,
  type ReportParameter,
} from '@/modules/analytics/api';
import { kpiTrendOption } from '@/modules/analytics/charts';
import { describeRange } from '@/modules/analytics/format';
import { CATEGORY_ICONS } from '@/modules/analytics/icons';
import { KpiTile, KpiTileSkeleton } from '@/modules/analytics/KpiTile';
import { CUSTOM_PERIOD, DEFAULT_PERIOD, FEATURED_REPORTS, PERIOD_PRESETS } from '@/modules/analytics/labels';
import { isIsoDate, previousRange, resolvePeriod, toSearchInputs } from '@/modules/analytics/params';
import { ReportParameterForm } from '@/modules/analytics/ReportInputs';

interface ExecutiveSearch {
  period?: string;
  from?: string;
  to?: string;
  facilityId?: string;
  trend?: string;
}

const FACILITY = /^([0-9a-fA-F-]{36}|all)$/;

export const Route = createFileRoute('/_app/analytics/')({
  validateSearch: (s: Record<string, unknown>): ExecutiveSearch => ({
    period: typeof s.period === 'string' && PERIOD_PRESETS.some((p) => p.value === s.period) ? s.period : undefined,
    from: typeof s.from === 'string' && isIsoDate(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && isIsoDate(s.to) ? s.to : undefined,
    facilityId: typeof s.facilityId === 'string' && FACILITY.test(s.facilityId) ? s.facilityId : undefined,
    trend: typeof s.trend === 'string' && /^[A-Za-z]{1,40}$/.test(s.trend) ? s.trend : undefined,
  }),
  component: ExecutiveDashboardPage,
});

/** The period and facility inputs of this screen, described like report parameters so the same form renders them. */
const FILTERS: ReportParameter[] = [
  {
    key: 'period',
    label: 'Period',
    kind: 'Period',
    required: false,
    defaultValue: DEFAULT_PERIOD,
    choices: null,
    min: null,
    max: null,
    hint: null,
  },
  {
    key: 'facilityId',
    label: 'Facility',
    kind: 'Facility',
    required: false,
    defaultValue: null,
    choices: null,
    min: null,
    max: null,
    hint: null,
  },
];

const useStyles = makeStyles({
  toolbar: { display: 'flex', alignItems: 'flex-end', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' },
  fresh: { color: tokens.colorNeutralForeground3, paddingBottom: '6px' },
  kpis: { display: 'grid', gap: '16px', gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))' },
  columns: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 1fr)',
    alignItems: 'start',
    '@media (max-width: 1000px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  links: { display: 'grid', gap: '8px', gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))' },
  linkCard: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    minWidth: 0,
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover, border: `1px solid ${tokens.colorBrandStroke2}` },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  linkIcon: { color: tokens.colorBrandForeground1, fontSize: '20px', display: 'flex', flexShrink: 0 },
  linkText: { display: 'flex', flexDirection: 'column', minWidth: 0, flex: 1 },
  ellipsis: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  legend: { color: tokens.colorNeutralForeground3 },
});

/** BI-01 executive dashboard: headline KPIs for a period against the period before, daily trends and shortcuts. */
function ExecutiveDashboardPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const today = todayIso();

  const period = search.period ?? (search.from && search.to ? CUSTOM_PERIOD : DEFAULT_PERIOD);
  const range =
    period === CUSTOM_PERIOD && search.from && search.to && search.from <= search.to
      ? { from: search.from, to: search.to }
      : (resolvePeriod(period === CUSTOM_PERIOD ? DEFAULT_PERIOD : period, today) ?? { from: today, to: today });
  const previous = previousRange(range.from, range.to);

  const kpis = useQuery(analyticsQueries.kpis(range.from, range.to, search.facilityId));
  const catalogue = useQuery(analyticsQueries.catalogue());
  const dashboards = useQuery(analyticsQueries.dashboards());

  const filterValues: ReportInputs = {
    period,
    ...(period === CUSTOM_PERIOD ? { from: range.from, to: range.to } : {}),
    ...(search.facilityId ? { facilityId: search.facilityId } : {}),
  };
  const setFilters = (next: ReportInputs) =>
    void navigate({
      search: { period: next.period, from: next.from, to: next.to, facilityId: next.facilityId, trend: search.trend },
    });

  const data = kpis.data;
  const withTrend = data?.kpis.filter((k) => (k.trend?.length ?? 0) > 1) ?? [];
  const selectedTrend = withTrend.find((k) => k.key === search.trend) ?? withTrend[0];
  const reports = catalogue.data?.reports ?? [];
  const featured = FEATURED_REPORTS.map((id) => reports.find((r) => r.id === id)).filter((r) => r !== undefined);
  const boards = [...(dashboards.data ?? [])]
    .sort((a, b) => Number(b.isTemplate) - Number(a.isTemplate) || a.name.localeCompare(b.name))
    .slice(0, 6);

  return (
    <Page>
      <PageHeader
        title="Executive dashboard"
        icon={<DataTrendingRegular />}
        subtitle={
          data
            ? `${data.facilityName} · ${describeRange(data.from, data.to)}, compared with ${describeRange(data.previousFrom, data.previousTo)}`
            : `${describeRange(range.from, range.to)}, compared with ${describeRange(previous.from, previous.to)}`
        }
        actions={
          <>
            <Tooltip content="Refresh the figures" relationship="label">
              <Button
                icon={<ArrowClockwiseRegular />}
                disabled={kpis.isFetching}
                onClick={() => void queryClient.invalidateQueries({ queryKey: analyticsKeys.kpis })}
              />
            </Tooltip>
            <Button icon={<BoardRegular />} onClick={() => void navigate({ to: '/analytics/dashboards' })}>
              Dashboards
            </Button>
            <Button
              appearance="primary"
              icon={<DocumentTableRegular />}
              onClick={() => void navigate({ to: '/analytics/reports' })}
            >
              MIS reports
            </Button>
          </>
        }
      />

      <div className={styles.toolbar}>
        <ReportParameterForm
          parameters={FILTERS}
          values={filterValues}
          onChange={setFilters}
          periods={catalogue.data?.periods ?? PERIOD_PRESETS}
          canConsolidate={catalogue.data?.canConsolidate ?? false}
          range={range}
          maxDays={366}
        />
        <Caption1 className={styles.fresh}>
          {data
            ? `Figures from the ${data.dataSource.toLowerCase()} · updated ${formatRelative(data.generatedAt)} · refreshed every 5 minutes`
            : ' '}
        </Caption1>
      </div>

      {kpis.error && !data ? (
        <ErrorState error={kpis.error} onRetry={() => void kpis.refetch()} title="Could not load the figures" />
      ) : data && data.kpis.length === 0 ? (
        <Section>
          <EmptyState
            title="No headline figures for your role"
            description="Your reports are still available in MIS reports."
            action={<Button onClick={() => void navigate({ to: '/analytics/reports' })}>Open MIS reports</Button>}
          />
        </Section>
      ) : (
        <>
          <div className={styles.kpis} aria-busy={kpis.isFetching}>
            {data
              ? data.kpis.map((kpi) => <KpiTile key={kpi.key} kpi={kpi} inputs={filterValues} />)
              : Array.from({ length: 8 }, (_, i) => <KpiTileSkeleton key={i} />)}
          </div>
          <Caption1 className={styles.legend}>
            Arrows compare with the previous period of the same length. Green means better, red means worse; percentages
            and scores change in points.
          </Caption1>
        </>
      )}

      <div className={styles.columns}>
        <Section
          title="Daily trend"
          description={selectedTrend ? selectedTrend.definition : 'Day-by-day figures for the chosen period'}
          actions={
            withTrend.length > 1 && (
              <TabList
                size="small"
                selectedValue={selectedTrend?.key}
                onTabSelect={(_, d) =>
                  void navigate({ search: (prev) => ({ ...prev, trend: String(d.value) }), replace: true })
                }
                aria-label="Figure to show"
              >
                {withTrend.map((k) => (
                  <Tab key={k.key} value={k.key}>
                    {k.label}
                  </Tab>
                ))}
              </TabList>
            )
          }
        >
          {selectedTrend ? (
            <TrendChart kpi={selectedTrend} />
          ) : (
            <Text className={styles.muted}>
              {kpis.isLoading ? 'Loading…' : 'Choose a period of two days or more to see the daily trend.'}
            </Text>
          )}
        </Section>

        <Section title="Dashboards" description="Saved views of your favourite reports">
          {boards.length === 0 ? (
            <Text className={styles.muted}>{dashboards.isLoading ? 'Loading…' : 'No dashboards yet.'}</Text>
          ) : (
            <div className={styles.links} style={{ gridTemplateColumns: 'minmax(0, 1fr)' }}>
              {boards.map((board) => (
                <Link
                  key={board.id}
                  to="/analytics/dashboards/$dashboardId"
                  params={{ dashboardId: board.id }}
                  className={styles.linkCard}
                >
                  <span className={styles.linkIcon}>
                    <BoardRegular />
                  </span>
                  <span className={styles.linkText}>
                    <Text weight="semibold" className={styles.ellipsis}>
                      {board.name}
                    </Text>
                    <Caption1 className={styles.muted}>
                      {board.isTemplate
                        ? 'Hospital template'
                        : board.isMine
                          ? 'Mine'
                          : `Shared by ${board.ownerName ?? 'a colleague'}`}{' '}
                      · {board.widgetCount} tiles
                    </Caption1>
                  </span>
                  <ChevronRightRegular aria-hidden />
                </Link>
              ))}
            </div>
          )}
        </Section>
      </div>

      {featured.length > 0 && (
        <Section title="Reports for this period" description="Open a report with the same period and facility">
          <div className={styles.links}>
            {featured.map((report) => {
              const Icon = CATEGORY_ICONS[report.category];
              return (
                <Link
                  key={report.id}
                  to="/analytics/reports/$reportId"
                  params={{ reportId: report.id }}
                  search={toSearchInputs(
                    report.parameters.some((p) => p.kind === 'Period') ? filterValues : withoutPeriod(filterValues),
                  )}
                  className={styles.linkCard}
                >
                  <span className={styles.linkIcon}>
                    <Icon />
                  </span>
                  <span className={styles.linkText}>
                    <Subtitle2 className={styles.ellipsis}>{report.title}</Subtitle2>
                    <Caption1 className={mergeClasses(styles.muted, styles.ellipsis)}>{report.description}</Caption1>
                  </span>
                </Link>
              );
            })}
          </div>
        </Section>
      )}
    </Page>
  );
}

function withoutPeriod(inputs: ReportInputs): ReportInputs {
  const next: ReportInputs = { ...inputs };
  delete next.period;
  delete next.from;
  delete next.to;
  return next;
}

function TrendChart({ kpi }: { kpi: KpiValue }) {
  const option = useMemo(() => kpiTrendOption(kpi), [kpi]);
  return <EChart option={option} height={280} ariaLabel={`${kpi.label} by day for the chosen period`} />;
}
