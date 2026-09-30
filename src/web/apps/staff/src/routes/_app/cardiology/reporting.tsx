import { Caption1, makeStyles, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { DataTable, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { PriorityBadge } from '@/modules/lab/badges';
import { DiagnosticReportBadge } from '@/modules/lab/common/DiagnosticReportBadge';
import { useDiagnosticsLive } from '@/modules/lab/common/live';
import { searchOneOf, searchPage } from '@/modules/lab/common/search';
import {
  cardiologyQueries,
  STUDY_TYPES,
  type CardiologyStudyListItem,
  type CardiologyStudyType,
} from '@/modules/cardiology/api';
import { StudyTypeBadge } from '@/modules/cardiology/badges';
import { studyTypeLongLabel } from '@/modules/cardiology/labels';

interface ReportingSearch {
  type?: CardiologyStudyType;
  page?: number;
}

export const Route = createFileRoute('/_app/cardiology/reporting')({
  validateSearch: (s: Record<string, unknown>): ReportingSearch => ({
    type: searchOneOf(s.type, STUDY_TYPES),
    page: searchPage(s.page),
  }),
  component: CardiologyReportingPage,
});

const LIVE = ['cardiology'] as const;

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
});

/** Cardiologist's worklist (SRS CAR-03/04): completed studies waiting for a report or sign-off, STAT first. */
function CardiologyReportingPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  useDiagnosticsLive(LIVE);
  const worklist = useQuery(cardiologyQueries.reportingWorklist(search.type, search.page));

  return (
    <Page>
      <PageHeader
        title="Cardiology reporting"
        subtitle={
          worklist.data
            ? `${worklist.data.totalCount} ${worklist.data.totalCount === 1 ? 'study' : 'studies'} waiting. STAT first, then the longest waiting.`
            : 'Studies waiting for a report'
        }
      />
      <TabList
        selectedValue={search.type ?? 'all'}
        onTabSelect={(_, data) =>
          void navigate({ search: { type: data.value === 'all' ? undefined : (data.value as CardiologyStudyType) } })
        }
      >
        <Tab value="all">All</Tab>
        {STUDY_TYPES.map((type) => (
          <Tab key={type} value={type}>
            {studyTypeLongLabel[type]}
          </Tab>
        ))}
      </TabList>
      <Section flush>
        <DataTable
          ariaLabel="Cardiology studies waiting for a report"
          rows={worklist.data?.items}
          loading={worklist.isLoading}
          error={worklist.error}
          onRetry={() => void worklist.refetch()}
          getRowId={(study) => study.id}
          onRowClick={(study) => void navigate({ to: '/cardiology/studies/$studyId', params: { studyId: study.id } })}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (study: CardiologyStudyListItem) => (
                <span className={styles.cell}>
                  <span className={styles.row}>
                    <Text weight="semibold">{study.patientName}</Text>
                    <PriorityBadge priority={study.priority} />
                  </span>
                  <Caption1 className={styles.muted}>
                    {study.age ?? '—'} / {study.gender.charAt(0)} · {study.uhid}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'study',
              header: 'Study',
              render: (study: CardiologyStudyListItem) => (
                <span className={styles.cell}>
                  <span className={styles.row}>
                    <StudyTypeBadge type={study.studyType} />
                    <Text>{study.studyName}</Text>
                  </span>
                  <Caption1 className={styles.muted}>{study.accessionNumber}</Caption1>
                </span>
              ),
            },
            {
              key: 'ordered',
              header: 'Ordered',
              priority: 'low',
              render: (study: CardiologyStudyListItem) => formatDateTime(study.orderedAt),
            },
            {
              key: 'report',
              header: 'Report',
              width: 170,
              render: (study: CardiologyStudyListItem) =>
                study.reportStatus ? (
                  <DiagnosticReportBadge status={study.reportStatus} />
                ) : (
                  <Caption1 className={styles.muted}>Not started</Caption1>
                ),
            },
          ]}
          emptyTitle="Nothing to report"
          emptyDescription="Studies appear here once the technician attaches the tracing and completes them."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={worklist.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
