import {
  Button,
  Caption1,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  SearchBox,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowSyncRegular,
  DocumentEditRegular,
  ImageMultipleRegular,
  MoreHorizontalRegular,
} from '@fluentui/react-icons';
import { formatDate, formatTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Can, usePermission } from '@/lib/permissions';
import { DateRangeFields } from '@/modules/lab/common/DateRangeFields';
import { useDiagnosticsLive } from '@/modules/lab/common/live';
import { searchDate, searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import { PriorityBadge } from '@/modules/lab/badges';
import {
  MODALITIES,
  RADIOLOGY_PERMISSIONS,
  radiologyApi,
  radiologyKeys,
  radiologyQueries,
  STUDY_STATUSES,
  type ImagingStudyListItem,
  type ImagingStudyStatus,
  type Modality,
} from '@/modules/radiology/api';
import { DiagnosticReportBadge, ModalityBadge, StudyFlags, StudyStatusBadge } from '@/modules/radiology/badges';
import { modalityLabel, studyStatusLabel } from '@/modules/radiology/labels';
import { useStudyActions } from '@/modules/radiology/useStudyActions';
import { nextStudyAction, studyActionLabel, studyActions } from '@/modules/radiology/workflow';

interface StudiesSearch {
  modality?: Modality;
  status?: ImagingStudyStatus;
  from?: string;
  to?: string;
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/radiology/')({
  validateSearch: (s: Record<string, unknown>): StudiesSearch => ({
    modality: searchOneOf(s.modality, MODALITIES),
    status: searchOneOf(s.status, STUDY_STATUSES),
    from: searchDate(s.from),
    to: searchDate(s.to),
    search: searchText(s.search, 60),
    page: searchPage(s.page),
  }),
  component: ImagingWorklistPage,
});

const LIVE = ['radiology'] as const;

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
  actions: { display: 'flex', gap: '4px', alignItems: 'center' },
});

/**
 * Imaging worklist (SRS IMG-01/02): studies by modality and status with the technologist's next step on each row,
 * PACS linking and the image viewer. STAT first, then by appointment time.
 */
function ImagingWorklistPage() {
  const styles = useStyles();
  // Closing a dialog opened from a row's buttons returns focus to the button that opened it.
  const restoreFocusTarget = useRestoreFocusTarget();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canWork = usePermission(RADIOLOGY_PERMISSIONS.worklist);
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const actions = useStudyActions();
  useDiagnosticsLive(LIVE);

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const studies = useQuery(
    radiologyQueries.studies({
      modality: search.modality,
      status: search.status,
      from: search.from,
      to: search.to,
      search: search.search,
      page: search.page,
    }),
  );
  const set = (patch: Partial<StudiesSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const today = todayIso();

  const reconcileAll = useMutation({
    mutationFn: () => radiologyApi.reconcileRecent(),
    onSuccess: (summary) => {
      notify.success(
        `${summary.linked} of ${summary.checked} studies linked to their images`,
        summary.messages.slice(0, 3).join(' · ') || undefined,
      );
      void queryClient.invalidateQueries({ queryKey: radiologyKeys.all });
    },
    onError: (error) => notify.error(error, 'PACS could not be checked'),
  });

  return (
    <Page>
      <PageHeader
        title="Imaging worklist"
        subtitle={
          search.from && search.from !== today
            ? `From ${formatDate(search.from)}${search.to && search.to !== search.from ? ` to ${formatDate(search.to)}` : ''}`
            : "Today's studies, live"
        }
        actions={
          <>
            <Can permission={[RADIOLOGY_PERMISSIONS.write, RADIOLOGY_PERMISSIONS.verify]}>
              <Button
                appearance="primary"
                icon={<DocumentEditRegular />}
                onClick={() => void navigate({ to: '/radiology/reporting' })}
              >
                Reporting worklist
              </Button>
            </Can>
            {canWork && (
              <Button
                icon={<ArrowSyncRegular />}
                disabled={reconcileAll.isPending}
                onClick={() => reconcileAll.mutate()}
              >
                Check PACS for images
              </Button>
            )}
          </>
        }
      />

      <TabList
        selectedValue={search.modality ?? 'all'}
        onTabSelect={(_, data) => set({ modality: data.value === 'all' ? undefined : (data.value as Modality) })}
      >
        <Tab value="all">All</Tab>
        {MODALITIES.map((modality) => (
          <Tab key={modality} value={modality}>
            {modalityLabel[modality]}
          </Tab>
        ))}
      </TabList>

      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox
                value={term}
                onChange={(_, data) => setTerm(data.value)}
                placeholder="Accession, UHID, name or mobile"
                autoFocus
              />
            </Field>
            <DateRangeFields
              value={{ from: search.from, to: search.to }}
              onChange={(range) => set(range)}
              allowFuture
            />
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, data) => set({ status: (data.value || undefined) as ImagingStudyStatus | undefined })}
              >
                <option value="">All</option>
                {STUDY_STATUSES.map((value) => (
                  <option key={value} value={value}>
                    {studyStatusLabel[value]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Imaging studies"
          rows={studies.data?.items}
          loading={studies.isLoading}
          error={studies.error}
          onRetry={() => void studies.refetch()}
          getRowId={(study) => study.id}
          onRowClick={(study) => void navigate({ to: '/radiology/studies/$studyId', params: { studyId: study.id } })}
          size="small"
          columns={[
            {
              key: 'when',
              header: 'When',
              width: 100,
              render: (study: ImagingStudyListItem) => (
                <span className={styles.cell}>
                  <Text>{formatTime(study.scheduledAt ?? study.orderedAt)}</Text>
                  <Caption1 className={styles.muted}>{study.scheduledAt ? 'appointment' : 'ordered'}</Caption1>
                </span>
              ),
            },
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
                  <Caption1 className={styles.muted}>
                    {study.accessionNumber}
                    {study.room ? ` · ${study.room}` : ''}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 160,
              render: (study: ImagingStudyListItem) => <StudyStatusBadge status={study.status} />,
            },
            {
              key: 'report',
              header: 'Report',
              width: 150,
              priority: 'low',
              render: (study: ImagingStudyListItem) => <DiagnosticReportBadge status={study.reportStatus} />,
            },
          ]}
          rowActions={(study: ImagingStudyListItem) => {
            const next = canWork ? nextStudyAction(study.status) : null;
            const ref = {
              id: study.id,
              studyName: study.studyName,
              patientName: study.patientName,
              status: study.status,
              scheduledAt: study.scheduledAt,
              room: study.room,
            };
            const others = canWork
              ? studyActions(study.status).filter((action) => action !== next && action !== 'report')
              : [];
            return (
              <span className={styles.actions}>
                {next && (
                  <Button
                    size="small"
                    appearance="primary"
                    disabled={actions.busy}
                    // Stays focusable while the step runs, so focus is not dropped to the page.
                    disabledFocusable={actions.busy}
                    onClick={() => actions.run(next, ref)}
                    {...restoreFocusTarget}
                  >
                    {studyActionLabel[next]}
                  </Button>
                )}
                <Menu>
                  <MenuTrigger disableButtonEnhancement>
                    <Button
                      size="small"
                      appearance="subtle"
                      icon={<MoreHorizontalRegular />}
                      aria-label={`More for ${study.studyName}, ${study.patientName}`}
                      {...restoreFocusTarget}
                    />
                  </MenuTrigger>
                  <MenuPopover>
                    <MenuList>
                      <MenuItem icon={<DocumentEditRegular />} onClick={() => actions.run('report', ref)}>
                        Open study and report
                      </MenuItem>
                      <MenuItem icon={<ImageMultipleRegular />} onClick={() => void actions.openImages(study.id)}>
                        View images
                      </MenuItem>
                      {others.map((action) => (
                        <MenuItem key={action} onClick={() => actions.run(action, ref)}>
                          {studyActionLabel[action]}
                        </MenuItem>
                      ))}
                    </MenuList>
                  </MenuPopover>
                </Menu>
              </span>
            );
          }}
          emptyTitle={search.search ? 'No study matches' : 'No studies for these dates'}
          emptyDescription="Imaging orders from doctors appear here as soon as they are placed."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={studies.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      {actions.dialogs}
    </Page>
  );
}
