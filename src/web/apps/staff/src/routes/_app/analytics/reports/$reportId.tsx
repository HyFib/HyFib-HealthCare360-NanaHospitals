import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  ProgressBar,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowUndoRegular, BoardRegular, BookInformationRegular, MailClockRegular, ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { AddToDashboardDialog } from '@/modules/analytics/AddToDashboardDialog';
import { analyticsQueries, type ReportChartType, type ReportInputs, type ReportResult } from '@/modules/analytics/api';
import { availableViews, defaultView } from '@/modules/analytics/charts';
import { ExportMenu } from '@/modules/analytics/ExportMenu';
import { formatCount } from '@/modules/analytics/format';
import { CATEGORY_ICONS } from '@/modules/analytics/icons';
import { DEFAULT_PAGE_SIZE } from '@/modules/analytics/labels';
import { drillInputs, inputsOf, parseReportSearch, scheduleInputs, toSearchInputs, withDefaults, type ReportSearch } from '@/modules/analytics/params';
import { ReportParameterForm } from '@/modules/analytics/ReportInputs';
import { ReportTable } from '@/modules/analytics/ReportTable';
import { ReportChart, ReportMeta, SummaryFigures, ViewSwitcher } from '@/modules/analytics/ReportView';

export const Route = createFileRoute('/_app/analytics/reports/$reportId')({
  validateSearch: (search: Record<string, unknown>): ReportSearch => parseReportSearch(search),
  component: ReportPage,
});

const useStyles = makeStyles({
  inputs: { display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' },
  definition: { maxWidth: '420px', display: 'flex', flexDirection: 'column', gap: '8px' },
  progress: { minHeight: '2px' },
  results: { display: 'flex', flexDirection: 'column', gap: '16px' },
  chart: { padding: '0 16px 8px' },
  notes: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '8px 16px 12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const CHART_VIEWS: ReadonlySet<ReportChartType> = new Set(['Bar', 'StackedBar', 'Line', 'Area', 'Pie']);

/** MIS-02..05: one report with its inputs, headline figures, chart, sortable table with totals, drill-down and export. */
function ReportPage() {
  const styles = useStyles();
  const { reportId } = Route.useParams();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const catalogue = useQuery(analyticsQueries.catalogue());
  const inputs = inputsOf(search);
  // Runs straight away, in parallel with the catalogue (the server applies the report's defaults).
  const run = useQuery(analyticsQueries.run(reportId, inputs, { page: search.page, pageSize: search.size, sort: search.sort, desc: search.desc, chart: search.view }));
  const [addToDashboard, setAddToDashboard] = useState(false);

  if (catalogue.isLoading) {
    return <LoadingState label="Opening the report…" />;
  }
  if (catalogue.error || !catalogue.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={catalogue.error} onRetry={() => void catalogue.refetch()} title="Could not load the report list" />
      </Page>
    );
  }

  const data = catalogue.data;
  const report = data.reports.find((r) => r.id === reportId);
  if (!report) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="Report not available" breadcrumbs={[{ label: 'MIS reports', onClick: () => void navigate({ to: '/analytics/reports' }) }, { label: 'Not available' }]} />
        <Section>
          <EmptyState
            title="This report is not available to you"
            description="It may have been renamed, or your role does not include it. Ask your administrator if you need it."
            action={<Button onClick={() => void navigate({ to: '/analytics/reports' })}>Browse reports</Button>}
          />
        </Section>
      </Page>
    );
  }

  const values = withDefaults(report.parameters, inputs);
  const result = run.data?.reportId === report.id ? run.data : undefined;
  const views = availableViews(report.chart, 'report');
  const view = search.view && views.includes(search.view) ? search.view : defaultView(report.chart);
  const validation = isApiError(run.error) && run.error.isValidation ? firstMessages(run.error.fieldErrors) : null;
  const Icon = CATEGORY_ICONS[report.category];
  const knownNames = Object.fromEntries(
    report.parameters.flatMap((p) => {
      const filter = result?.filters.find((f) => f.label === p.label);
      return filter ? [[p.key, filter.value]] : [];
    }),
  );

  const go = (patch: Partial<ReportSearch>, replace = false) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace });
  const changeInputs = (next: ReportInputs) => {
    // A different "show by" changes the columns, so the old sort no longer applies.
    const keepSort = next.groupBy === values.groupBy;
    void navigate({
      search: {
        ...toSearchInputs(next),
        view: search.view,
        size: search.size,
        sort: keepSort ? search.sort : undefined,
        desc: keepSort ? search.desc : undefined,
      },
    });
  };
  const drill = (current: ReportResult, key: string) => {
    if (!current.drillDown) {
      return;
    }
    void navigate({
      to: '/analytics/reports/$reportId',
      params: { reportId: current.drillDown.reportId },
      search: toSearchInputs(drillInputs(current.parameters, current.drillDown, key)),
    });
  };
  const truncatedNote = result?.truncated ? result.notes.at(-1) : undefined;
  const notes = result ? (result.truncated ? result.notes.slice(0, -1) : result.notes) : [];

  return (
    <Page>
      <PageHeader
        title={report.title}
        icon={<Icon />}
        subtitle={report.description}
        breadcrumbs={[
          { label: 'Analytics', onClick: () => void navigate({ to: '/analytics' }) },
          { label: 'MIS reports', onClick: () => void navigate({ to: '/analytics/reports' }) },
          { label: report.categoryLabel, onClick: () => void navigate({ to: '/analytics/reports', search: { category: report.category } }) },
          { label: report.title },
        ]}
        actions={
          <>
            <Popover withArrow positioning="below-end">
              <PopoverTrigger disableButtonEnhancement>
                <Button appearance="subtle" icon={<BookInformationRegular />}>
                  How it is calculated
                </Button>
              </PopoverTrigger>
              <PopoverSurface aria-label="How this report is calculated">
                <div className={styles.definition}>
                  <Subtitle2>{report.title}</Subtitle2>
                  <Text>{report.definition}</Text>
                  <Caption1 className={styles.muted}>Dates are hospital days in IST. Cancelled and deleted records are left out.</Caption1>
                </div>
              </PopoverSurface>
            </Popover>
            {data.canManageSchedules && report.canSchedule && (
              <Button
                icon={<MailClockRegular />}
                onClick={() => void navigate({ to: '/analytics/schedules', search: { create: report.id, inputs: scheduleInputs(result?.parameters ?? values) } })}
              >
                E-mail regularly
              </Button>
            )}
            {data.canManageDashboards && !report.containsPatientIdentifiers && (
              <Button icon={<BoardRegular />} disabled={!result} onClick={() => setAddToDashboard(true)}>
                Add to dashboard
              </Button>
            )}
            {result ? (
              <ExportMenu result={result} appearance="primary" />
            ) : (
              <Button appearance="primary" disabled>
                Export
              </Button>
            )}
          </>
        }
      />

      {report.containsPatientIdentifiers && (
        <MessageBar intent="warning" layout="multiline" icon={<ShieldCheckmarkRegular />}>
          <MessageBarBody>
            <MessageBarTitle>This report lists patients</MessageBarTitle>
            Each page you open and each export is recorded in the audit log with your name. Use it only for care, quality or statutory work.
          </MessageBarBody>
        </MessageBar>
      )}

      <Section>
        <div className={styles.inputs}>
          <ReportParameterForm
            parameters={report.parameters}
            values={values}
            onChange={changeInputs}
            periods={data.periods}
            errors={validation ?? undefined}
            canConsolidate={data.canConsolidate}
            range={result ? { from: result.from, to: result.to } : null}
            knownNames={knownNames}
            maxDays={report.maxDays}
          />
          {Object.keys(inputs).length > 0 && (
            <Button appearance="subtle" icon={<ArrowUndoRegular />} onClick={() => void navigate({ search: { view: search.view } })}>
              Reset inputs
            </Button>
          )}
        </div>
      </Section>

      {validation && (
        <MessageBar intent="error">
          <MessageBarBody>Please correct the highlighted inputs to run the report.</MessageBarBody>
        </MessageBar>
      )}

      {!result ? (
        run.error && !validation ? (
          <ErrorState error={run.error} onRetry={() => void run.refetch()} title="Could not run the report" />
        ) : validation ? null : (
          <Section>
            <LoadingState label="Running the report…" />
          </Section>
        )
      ) : (
        <div className={styles.results} aria-busy={run.isFetching}>
          <ReportMeta result={result} />
          {truncatedNote && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>{truncatedNote}</MessageBarBody>
            </MessageBar>
          )}
          <SummaryFigures figures={result.summary} />
          <Section
            flush
            title={formatCount(result.totalRows, 'row')}
            description={result.drillDown && result.rowKeys ? `Select a row to see ${result.drillDown.label.toLowerCase()}.` : undefined}
            actions={views.length > 1 && <ViewSwitcher views={views} value={view} onChange={(next) => go({ view: next === defaultView(report.chart) ? undefined : next }, true)} />}
          >
            <div className={styles.progress}>{run.isFetching && <ProgressBar thickness="medium" aria-label="Updating the report" />}</div>
            {CHART_VIEWS.has(view) && result.chart && result.rows.length > 0 && (
              <div className={styles.chart}>
                <ReportChart chart={result.chart} title={report.title} height={result.chart.type === 'Pie' ? 300 : 320} />
              </div>
            )}
            <ReportTable
              result={result}
              onSort={(key, desc) => go({ sort: key, desc, page: undefined })}
              onPage={(page) => go({ page: page > 1 ? page : undefined })}
              onPageSize={(size) => go({ size: size === DEFAULT_PAGE_SIZE ? undefined : size, page: undefined })}
              onDrill={(key) => drill(result, key)}
            />
            {notes.length > 0 && (
              <div className={styles.notes}>
                {notes.map((note) => (
                  <Caption1 key={note} className={styles.muted}>
                    {note}
                  </Caption1>
                ))}
              </div>
            )}
          </Section>
        </div>
      )}

      {addToDashboard && result && <AddToDashboardDialog report={report} result={result} view={view} onClose={() => setAddToDashboard(false)} />}
    </Page>
  );
}

function firstMessages(errors: Record<string, string[]>): Record<string, string> {
  return Object.fromEntries(Object.entries(errors).map(([key, messages]) => [key, messages[0] ?? 'Check this input.']));
}
