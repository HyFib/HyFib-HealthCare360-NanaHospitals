import {
  Badge,
  Button,
  CounterBadge,
  makeStyles,
  SearchBox,
  Subtitle2,
  Tab,
  TabList,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { BoardRegular, DocumentTableRegular, LockClosedRegular, MailClockRegular } from '@fluentui/react-icons';
import { useDebouncedValue } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { analyticsQueries, type ReportCatalogueItem, type ReportCategory } from '@/modules/analytics/api';
import { CATEGORY_ICONS } from '@/modules/analytics/icons';
import { CATEGORY_ORDER } from '@/modules/analytics/labels';

interface CatalogueSearch {
  q?: string;
  category?: ReportCategory;
}

export const Route = createFileRoute('/_app/analytics/reports/')({
  validateSearch: (s: Record<string, unknown>): CatalogueSearch => ({
    q: typeof s.q === 'string' && s.q.trim() ? s.q.slice(0, 100) : undefined,
    category: CATEGORY_ORDER.includes(s.category as ReportCategory) ? (s.category as ReportCategory) : undefined,
  }),
  component: ReportCataloguePage,
});

const useStyles = makeStyles({
  filters: { display: 'flex', flexDirection: 'column', gap: '10px' },
  search: { maxWidth: '560px', width: '100%' },
  tabs: { overflowX: 'auto' },
  grid: { display: 'grid', gap: '10px', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    minWidth: 0,
    transitionProperty: 'border-color, box-shadow',
    transitionDuration: tokens.durationFast,
    ':hover': { border: `1px solid ${tokens.colorBrandStroke2}`, boxShadow: tokens.shadow4 },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
  },
  cardHead: { display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: '8px' },
  description: {
    color: tokens.colorNeutralForeground2,
    display: '-webkit-box',
    WebkitLineClamp: 2,
    WebkitBoxOrient: 'vertical',
    overflow: 'hidden',
  },
  badges: { display: 'flex', gap: '4px', flexWrap: 'wrap', marginTop: '4px' },
  sectionTitle: { display: 'inline-flex', alignItems: 'center', gap: '8px' },
  sectionIcon: { color: tokens.colorBrandForeground1, display: 'flex', fontSize: '20px' },
  sections: { display: 'flex', flexDirection: 'column', gap: '16px' },
  lock: { color: tokens.colorPaletteMarigoldForeground1, flexShrink: 0, display: 'flex', fontSize: '18px' },
});

/** MIS-01: every report the user may open, by department, with a search that also reads descriptions and formulas. */
function ReportCataloguePage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const catalogue = useQuery(analyticsQueries.catalogue());
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 250);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  if (catalogue.isLoading) {
    return <LoadingState label="Loading reports…" />;
  }
  if (catalogue.error || !catalogue.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState
          error={catalogue.error}
          onRetry={() => void catalogue.refetch()}
          title="Could not load the reports"
        />
      </Page>
    );
  }

  const data = catalogue.data;
  const words = (search.q ?? '').toLowerCase().split(/\s+/).filter(Boolean);
  const matches = data.reports.filter((r) => words.every((w) => searchable(r).includes(w)));
  const inCategory = search.category ? matches.filter((r) => r.category === search.category) : matches;
  const counts = new Map<ReportCategory, number>();
  for (const report of matches) {
    counts.set(report.category, (counts.get(report.category) ?? 0) + 1);
  }

  return (
    <Page>
      <PageHeader
        title="MIS reports"
        icon={<DocumentTableRegular />}
        subtitle={`${data.reports.length} reports you can open · figures from the ${data.dataSource.toLowerCase()} · every report exports to Excel`}
        breadcrumbs={[
          { label: 'Analytics', onClick: () => void navigate({ to: '/analytics' }) },
          { label: 'MIS reports' },
        ]}
        actions={
          <>
            <Button icon={<BoardRegular />} onClick={() => void navigate({ to: '/analytics/dashboards' })}>
              Dashboards
            </Button>
            {data.canManageSchedules && (
              <Button icon={<MailClockRegular />} onClick={() => void navigate({ to: '/analytics/schedules' })}>
                Scheduled e-mails
              </Button>
            )}
          </>
        }
      />

      <div className={styles.filters}>
        <SearchBox
          className={styles.search}
          value={term}
          onChange={(_, d) => setTerm(d.value)}
          placeholder="Search reports, e.g. occupancy, TAT, revenue, no-show"
          aria-label="Search reports"
          autoFocus
        />
        <div className={styles.tabs}>
          <TabList
            size="small"
            selectedValue={search.category ?? 'all'}
            onTabSelect={(_, d) =>
              void navigate({
                search: (prev) => ({ ...prev, category: d.value === 'all' ? undefined : (d.value as ReportCategory) }),
                replace: true,
              })
            }
            aria-label="Report areas"
          >
            <Tab value="all">All ({matches.length})</Tab>
            {data.categories.map((category) => {
              const Icon = CATEGORY_ICONS[category.id];
              return (
                <Tab key={category.id} value={category.id} icon={<Icon />} disabled={!counts.get(category.id)}>
                  {category.label} ({counts.get(category.id) ?? 0})
                </Tab>
              );
            })}
          </TabList>
        </div>
      </div>

      {inCategory.length === 0 ? (
        <Section>
          <EmptyState
            title={search.q ? `No report matches “${search.q}”` : 'No reports in this area'}
            description="Try another word, such as census, occupancy, revenue or turnaround."
            action={
              <Button
                onClick={() => {
                  setTerm('');
                  void navigate({ search: {} });
                }}
              >
                Show all reports
              </Button>
            }
          />
        </Section>
      ) : (
        <div className={styles.sections}>
          {CATEGORY_ORDER.map((category) => {
            const reports = inCategory.filter((r) => r.category === category);
            if (reports.length === 0) {
              return null;
            }
            const Icon = CATEGORY_ICONS[category];
            return (
              <Section
                key={category}
                title={
                  <span className={styles.sectionTitle}>
                    <span className={styles.sectionIcon}>
                      <Icon />
                    </span>
                    {reports[0]?.categoryLabel}
                    <CounterBadge count={reports.length} appearance="ghost" color="informative" size="small" />
                  </span>
                }
              >
                <div className={styles.grid}>
                  {reports.map((report) => (
                    <ReportCard key={report.id} report={report} />
                  ))}
                </div>
              </Section>
            );
          })}
        </div>
      )}
    </Page>
  );
}

function ReportCard({ report }: { report: ReportCatalogueItem }) {
  const styles = useStyles();
  return (
    <Link to="/analytics/reports/$reportId" params={{ reportId: report.id }} className={styles.card}>
      <span className={styles.cardHead}>
        <Subtitle2>{report.title}</Subtitle2>
        {report.containsPatientIdentifiers && (
          <Tooltip
            content="Lists patients: every view and export is recorded in the audit log"
            relationship="description"
          >
            <span className={styles.lock} aria-label="Lists patients">
              <LockClosedRegular />
            </span>
          </Tooltip>
        )}
      </span>
      <Text size={200} className={styles.description}>
        {report.description}
      </Text>
      <span className={styles.badges}>
        {report.containsPatientIdentifiers ? (
          <Badge appearance="tint" color="warning" size="small">
            Patient list
          </Badge>
        ) : report.canSchedule ? (
          <Badge appearance="tint" color="informative" size="small">
            Can be e-mailed
          </Badge>
        ) : null}
        {!report.parameters.some((p) => p.kind === 'Period') && (
          <Badge appearance="tint" color="subtle" size="small">
            Current position
          </Badge>
        )}
      </span>
    </Link>
  );
}

function searchable(report: ReportCatalogueItem): string {
  return `${report.title} ${report.description} ${report.definition} ${report.categoryLabel} ${report.id.replaceAll('-', ' ')}`.toLowerCase();
}
