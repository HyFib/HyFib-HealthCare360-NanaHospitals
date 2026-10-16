import { Badge, Caption1, Link as FluentLink, makeStyles, SearchBox, Text, tokens } from '@fluentui/react-components';
import { BookInformationRegular } from '@fluentui/react-icons';
import { useDebouncedValue } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { analyticsQueries, type KpiDefinition } from '@/modules/analytics/api';
import { filterDefinitions, kpiDirection, kpiUnitLabel } from '@/modules/analytics/definitions';
import { ANY_ANALYTICS } from '@/modules/analytics/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

interface DefinitionsSearch {
  q?: string;
}

export const Route = createFileRoute('/_app/analytics/kpis')({
  validateSearch: (s: Record<string, unknown>): DefinitionsSearch => ({
    q: typeof s.q === 'string' && s.q.trim() ? s.q.slice(0, 100) : undefined,
  }),
  component: () => (
    <RequirePermission permission={ANY_ANALYTICS} what="the KPI definitions">
      <KpiDefinitionsPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  search: { maxWidth: '560px', width: '100%' },
  formula: { color: tokens.colorNeutralForeground2 },
  badges: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
});

/**
 * BI-01..05: what every KPI on the executive dashboard and every MIS report means, how it is calculated, where its
 * figures come from and which report holds the detail. Read from the same definitions as the dashboard and reports.
 */
function KpiDefinitionsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const definitions = useQuery(analyticsQueries.definitions());
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 250);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: { q: debounced || undefined }, replace: true });
    }
  }, [debounced, search.q, navigate]);

  const crumbs = [
    { label: 'Analytics', onClick: () => void navigate({ to: '/analytics' }) },
    { label: 'KPI definitions' },
  ];

  if (definitions.isLoading) {
    return <LoadingState label="Loading the definitions…" />;
  }
  if (definitions.error || !definitions.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="KPI definitions" icon={<BookInformationRegular />} breadcrumbs={crumbs} />
        <ErrorState
          error={definitions.error}
          onRetry={() => void definitions.refetch()}
          title="Could not load the definitions"
        />
      </Page>
    );
  }

  const shown = filterDefinitions(definitions.data, search.q);
  const nothing = shown.kpis.length === 0 && shown.reports.length === 0;
  const openReport = (reportId: string) => void navigate({ to: '/analytics/reports/$reportId', params: { reportId } });
  const kpiLabel = new Map(definitions.data.kpis.map((k) => [k.key, k.label]));

  return (
    <Page>
      <PageHeader
        title="KPI definitions"
        icon={<BookInformationRegular />}
        subtitle={`${definitions.data.kpis.length} KPIs and ${definitions.data.reports.length} reports you can see: how each is calculated and where its figures come from`}
        breadcrumbs={crumbs}
      />

      <SearchBox
        className={styles.search}
        value={term}
        onChange={(_, d) => setTerm(d.value)}
        placeholder="Search, e.g. occupancy, median, collections, no-show"
        aria-label="Search KPIs and reports"
        autoFocus
      />

      {nothing ? (
        <Section>
          <EmptyState
            title={`Nothing matches “${search.q ?? ''}”`}
            description="Try another word, such as revenue, turnaround, census or feedback."
          />
        </Section>
      ) : (
        <>
          {shown.kpis.length > 0 && (
            <Section title="Executive dashboard KPIs">
              <DataTable<KpiDefinition>
                ariaLabel="KPI definitions"
                rows={shown.kpis}
                getRowId={(k) => k.key}
                onRowClick={(k) => k.reportTitle && openReport(k.reportId)}
                columns={[
                  {
                    key: 'kpi',
                    header: 'KPI',
                    width: 220,
                    render: (k) => (
                      <>
                        <Text block weight="semibold">
                          {k.label}
                        </Text>
                        <Caption1>
                          {kpiUnitLabel[k.unit]} · {kpiDirection(k)}
                        </Caption1>
                      </>
                    ),
                  },
                  {
                    key: 'formula',
                    header: 'How it is calculated',
                    render: (k) => <Text className={styles.formula}>{k.formula}</Text>,
                  },
                  { key: 'source', header: 'Figures from', priority: 'low', render: (k) => k.source },
                  {
                    key: 'report',
                    header: 'Detail in',
                    render: (k) =>
                      k.reportTitle ? (
                        <FluentLink
                          onClick={(event) => {
                            event.stopPropagation();
                            openReport(k.reportId);
                          }}
                        >
                          {k.reportTitle}
                        </FluentLink>
                      ) : (
                        <Caption1>A report you cannot open</Caption1>
                      ),
                  },
                ]}
              />
            </Section>
          )}
          {shown.reports.length > 0 && (
            <Section title="MIS reports">
              <DataTable
                ariaLabel="Report definitions"
                rows={shown.reports}
                getRowId={(r) => r.id}
                onRowClick={(r) => openReport(r.id)}
                columns={[
                  {
                    key: 'report',
                    header: 'Report',
                    width: 240,
                    render: (r) => (
                      <>
                        <Text block weight="semibold">
                          {r.title}
                        </Text>
                        <Caption1>{r.categoryLabel}</Caption1>
                      </>
                    ),
                  },
                  {
                    key: 'definition',
                    header: 'What it shows and how',
                    render: (r) => (
                      <>
                        <Text block>{r.description}</Text>
                        <Caption1 className={styles.formula}>{r.definition}</Caption1>
                      </>
                    ),
                  },
                  {
                    key: 'notes',
                    header: 'Notes',
                    priority: 'low',
                    render: (r) => (
                      <span className={styles.badges}>
                        {r.kpiKeys.map((key) => (
                          <Badge key={key} appearance="tint" color="brand" size="small">
                            KPI: {kpiLabel.get(key) ?? key}
                          </Badge>
                        ))}
                        {r.containsPatientIdentifiers && (
                          <Badge appearance="tint" color="warning" size="small">
                            Patient list
                          </Badge>
                        )}
                      </span>
                    ),
                  },
                ]}
              />
            </Section>
          )}
        </>
      )}
    </Page>
  );
}
