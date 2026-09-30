import { Caption1, makeStyles, Tab, TabList, Text, tokens } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { DataTable, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { PriorityBadge } from '@/modules/lab/badges';
import { useDiagnosticsLive } from '@/modules/lab/common/live';
import { searchOneOf, searchPage } from '@/modules/lab/common/search';
import { MODALITIES, radiologyQueries, type ImagingStudyListItem, type Modality } from '@/modules/radiology/api';
import { DiagnosticReportBadge, ModalityBadge, StudyFlags } from '@/modules/radiology/badges';
import { modalityLabel } from '@/modules/radiology/labels';

interface ReportingSearch {
  modality?: Modality;
  page?: number;
}

export const Route = createFileRoute('/_app/radiology/reporting')({
  validateSearch: (s: Record<string, unknown>): ReportingSearch => ({
    modality: searchOneOf(s.modality, MODALITIES),
    page: searchPage(s.page),
  }),
  component: ReportingWorklistPage,
});

const LIVE = ['radiology'] as const;

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
});

/**
 * Radiologist's worklist (SRS IMG-03/05): scanned studies waiting for a report or sign-off. Urgent AI findings come
 * first, then STAT, then the longest waiting.
 */
function ReportingWorklistPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  useDiagnosticsLive(LIVE);
  const worklist = useQuery(radiologyQueries.reportingWorklist(search.modality, search.page));

  return (
    <Page>
      <PageHeader
        title="Reporting worklist"
        subtitle={
          worklist.data
            ? `${worklist.data.totalCount} ${worklist.data.totalCount === 1 ? 'study' : 'studies'} waiting. Urgent AI findings and STAT studies first.`
            : 'Studies waiting for a report'
        }
      />
      <TabList
        selectedValue={search.modality ?? 'all'}
        onTabSelect={(_, data) =>
          void navigate({ search: { modality: data.value === 'all' ? undefined : (data.value as Modality) } })
        }
      >
        <Tab value="all">All</Tab>
        {MODALITIES.map((modality) => (
          <Tab key={modality} value={modality}>
            {modalityLabel[modality]}
          </Tab>
        ))}
      </TabList>
      <Section flush>
        <DataTable
          ariaLabel="Studies waiting for a report"
          rows={worklist.data?.items}
          loading={worklist.isLoading}
          error={worklist.error}
          onRetry={() => void worklist.refetch()}
          getRowId={(study) => study.id}
          onRowClick={(study) => void navigate({ to: '/radiology/studies/$studyId', params: { studyId: study.id } })}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (study: ImagingStudyListItem) => (
                <span className={styles.cell}>
                  <span className={styles.row}>
                    <Text weight="semibold">{study.patientName}</Text>
                    <PriorityBadge priority={study.priority} />
                    <StudyFlags urgentAi={study.hasUrgentAiFinding} critical={study.isCriticalFinding} />
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
              render: (study: ImagingStudyListItem) => (
                <span className={styles.cell}>
                  <span className={styles.row}>
                    <ModalityBadge modality={study.modality} />
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
              render: (study: ImagingStudyListItem) => formatDateTime(study.orderedAt),
            },
            {
              key: 'report',
              header: 'Report',
              width: 170,
              render: (study: ImagingStudyListItem) =>
                study.reportStatus ? (
                  <DiagnosticReportBadge status={study.reportStatus} />
                ) : (
                  <Caption1 className={styles.muted}>Not started</Caption1>
                ),
            },
          ]}
          emptyTitle="Nothing to report"
          emptyDescription="Scanned studies appear here as soon as the technologist completes them."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={worklist.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
