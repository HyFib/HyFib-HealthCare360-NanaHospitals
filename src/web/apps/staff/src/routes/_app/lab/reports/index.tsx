import { Caption1, Field, makeStyles, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import { formatDate, formatDateTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { labQueries, REPORT_STATUSES, type LabReportListItem, type LabReportStatus } from '@/modules/lab/api';
import { ReportStatusBadge } from '@/modules/lab/badges';
import { DateRangeFields } from '@/modules/lab/common/DateRangeFields';
import { searchDate, searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { reportStatusLabel } from '@/modules/lab/labels';
import { useLabLive } from '@/modules/lab/useLabLive';

interface ReportsSearch {
  search?: string;
  from?: string;
  to?: string;
  status?: LabReportStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/lab/reports/')({
  validateSearch: (s: Record<string, unknown>): ReportsSearch => ({
    search: searchText(s.search, 60),
    from: searchDate(s.from),
    to: searchDate(s.to),
    status: searchOneOf(s.status, REPORT_STATUSES),
    page: searchPage(s.page),
  }),
  component: LabReportsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
});

/** Lab reports by order date: ready to release, released, amended (SRS LIS-07). */
function LabReportsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  useLabLive();
  const reports = useQuery(
    labQueries.reports({
      search: search.search,
      from: search.from,
      to: search.to,
      status: search.status,
      page: search.page,
    }),
  );
  const set = (patch: Partial<ReportsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const today = todayIso();

  return (
    <Page>
      <PageHeader
        title="Lab reports"
        subtitle={
          search.from && search.from !== today
            ? `Orders from ${formatDate(search.from)}${search.to && search.to !== search.from ? ` to ${formatDate(search.to)}` : ''}`
            : "Today's orders"
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, data) => setTerm(data.value)}
                placeholder="Report number, UHID, name or mobile"
                autoFocus
              />
            </Field>
            <DateRangeFields value={{ from: search.from, to: search.to }} onChange={(range) => set(range)} />
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, data) => set({ status: (data.value || undefined) as LabReportStatus | undefined })}
              >
                <option value="">All</option>
                {REPORT_STATUSES.map((value) => (
                  <option key={value} value={value}>
                    {reportStatusLabel[value]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Lab reports"
          rows={reports.data?.items}
          loading={reports.isLoading}
          error={reports.error}
          onRetry={() => void reports.refetch()}
          getRowId={(report) => report.id}
          onRowClick={(report) => void navigate({ to: '/lab/reports/$reportId', params: { reportId: report.id } })}
          columns={[
            {
              key: 'number',
              header: 'Report',
              render: (report: LabReportListItem) => (
                <span className={styles.cell}>
                  <Text className={styles.mono}>{report.reportNumber}</Text>
                  {report.revision > 1 && <Caption1 className={styles.muted}>Revision {report.revision}</Caption1>}
                </span>
              ),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (report: LabReportListItem) => (
                <span className={styles.cell}>
                  <Text weight="semibold">{report.patientName}</Text>
                  <Caption1 className={styles.muted}>{report.uhid}</Caption1>
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 190,
              render: (report: LabReportListItem) => (
                <ReportStatusBadge status={report.status} partial={report.isPartial} />
              ),
            },
            {
              key: 'ordered',
              header: 'Ordered',
              priority: 'low',
              render: (report: LabReportListItem) => formatDateTime(report.orderedAt),
            },
            {
              key: 'released',
              header: 'Released',
              priority: 'low',
              render: (report: LabReportListItem) => (report.releasedAt ? formatDateTime(report.releasedAt) : '—'),
            },
          ]}
          emptyTitle={search.search ? 'No report matches your search' : 'No reports for these dates'}
          emptyDescription="A report is created with each lab order and fills in as results are verified."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={reports.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
    </Page>
  );
}
