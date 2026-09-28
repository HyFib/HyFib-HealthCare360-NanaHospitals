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
} from '@fluentui/react-components';
import { DocumentEditRegular, MoreHorizontalRegular } from '@fluentui/react-icons';
import { formatDate, formatTime, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Can, usePermission } from '@/lib/permissions';
import { PriorityBadge } from '@/modules/lab/badges';
import { DiagnosticReportBadge } from '@/modules/lab/common/DiagnosticReportBadge';
import { DateRangeFields } from '@/modules/lab/common/DateRangeFields';
import { useDiagnosticsLive } from '@/modules/lab/common/live';
import { searchDate, searchOneOf, searchPage, searchText } from '@/modules/lab/common/search';
import {
  CARDIOLOGY_PERMISSIONS,
  CARDIOLOGY_STATUSES,
  cardiologyQueries,
  STUDY_TYPES,
  type CardiologyStudyListItem,
  type CardiologyStudyStatus,
  type CardiologyStudyType,
} from '@/modules/cardiology/api';
import { CardiologyStatusBadge, StudyTypeBadge } from '@/modules/cardiology/badges';
import { cardiologyStatusLabel, studyTypeLongLabel } from '@/modules/cardiology/labels';
import { useCardiologyActions } from '@/modules/cardiology/useCardiologyActions';
import { cardiologyActionLabel, cardiologyActions, nextCardiologyAction } from '@/modules/cardiology/workflow';

interface CardiologySearch {
  type?: CardiologyStudyType;
  status?: CardiologyStudyStatus;
  from?: string;
  to?: string;
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/cardiology/')({
  validateSearch: (s: Record<string, unknown>): CardiologySearch => ({
    type: searchOneOf(s.type, STUDY_TYPES),
    status: searchOneOf(s.status, CARDIOLOGY_STATUSES),
    from: searchDate(s.from),
    to: searchDate(s.to),
    search: searchText(s.search, 60),
    page: searchPage(s.page),
  }),
  component: CardiologyStudiesPage,
});

const LIVE = ['cardiology'] as const;

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
  actions: { display: 'flex', gap: '4px', alignItems: 'center' },
});

/**
 * Cardiology studies (SRS CAR-01/02): ECG, echo, TMT, Holter and ABPM orders with the technician's next step on each
 * row (start, attach the tracing, complete).
 */
function CardiologyStudiesPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(CARDIOLOGY_PERMISSIONS.manage);
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const actions = useCardiologyActions();
  useDiagnosticsLive(LIVE);

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  const studies = useQuery(
    cardiologyQueries.studies({ studyType: search.type, status: search.status, from: search.from, to: search.to, search: search.search, page: search.page }),
  );
  const set = (patch: Partial<CardiologySearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const today = todayIso();

  return (
    <Page>
      <PageHeader
        title="Cardiology studies"
        subtitle={search.from && search.from !== today ? `From ${formatDate(search.from)}${search.to && search.to !== search.from ? ` to ${formatDate(search.to)}` : ''}` : "Today's ECG, echo and stress tests"}
        actions={
          <Can permission={CARDIOLOGY_PERMISSIONS.verify}>
            <Button appearance="primary" icon={<DocumentEditRegular />} onClick={() => void navigate({ to: '/cardiology/reporting' })}>
              Reporting worklist
            </Button>
          </Can>
        }
      />
      <TabList selectedValue={search.type ?? 'all'} onTabSelect={(_, data) => set({ type: data.value === 'all' ? undefined : (data.value as CardiologyStudyType) })}>
        <Tab value="all">All</Tab>
        {STUDY_TYPES.map((type) => (
          <Tab key={type} value={type}>
            {studyTypeLongLabel[type]}
          </Tab>
        ))}
      </TabList>
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, data) => setTerm(data.value)} placeholder="Accession, UHID, name or mobile" autoFocus />
            </Field>
            <DateRangeFields value={{ from: search.from, to: search.to }} onChange={(range) => set(range)} allowFuture />
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, data) => set({ status: (data.value || undefined) as CardiologyStudyStatus | undefined })}>
                <option value="">All</option>
                {CARDIOLOGY_STATUSES.map((value) => (
                  <option key={value} value={value}>
                    {cardiologyStatusLabel[value]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Cardiology studies"
          rows={studies.data?.items}
          loading={studies.isLoading}
          error={studies.error}
          onRetry={() => void studies.refetch()}
          getRowId={(study) => study.id}
          onRowClick={(study) => void navigate({ to: '/cardiology/studies/$studyId', params: { studyId: study.id } })}
          size="small"
          columns={[
            {
              key: 'when',
              header: 'When',
              width: 100,
              render: (study: CardiologyStudyListItem) => (
                <span className={styles.cell}>
                  <Text>{formatTime(study.scheduledAt ?? study.orderedAt)}</Text>
                  <Caption1 className={styles.muted}>{study.scheduledAt ? 'appointment' : 'ordered'}</Caption1>
                </span>
              ),
            },
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
            { key: 'status', header: 'Status', width: 160, render: (study: CardiologyStudyListItem) => <CardiologyStatusBadge status={study.status} /> },
            { key: 'report', header: 'Report', width: 150, priority: 'low', render: (study: CardiologyStudyListItem) => <DiagnosticReportBadge status={study.reportStatus} /> },
          ]}
          rowActions={(study: CardiologyStudyListItem) => {
            // The list does not carry the file count, so completion is offered on the study page, which checks the tracing.
            const next = canManage ? nextCardiologyAction(study.status, 0) : null;
            const ref = { id: study.id, patientId: study.patientId, studyName: study.studyName, studyType: study.studyType, patientName: study.patientName, status: study.status, scheduledAt: study.scheduledAt, fileCount: 0 };
            const others = canManage ? cardiologyActions(study.status).filter((action) => action !== next && action !== 'complete') : [];
            return (
              <span className={styles.actions}>
                {next && (
                  <Button size="small" appearance="primary" onClick={() => actions.run(next, ref)}>
                    {cardiologyActionLabel[next]}
                  </Button>
                )}
                <Menu>
                  <MenuTrigger disableButtonEnhancement>
                    <Button size="small" appearance="subtle" icon={<MoreHorizontalRegular />} aria-label={`More for ${study.studyName}, ${study.patientName}`} />
                  </MenuTrigger>
                  <MenuPopover>
                    <MenuList>
                      <MenuItem icon={<DocumentEditRegular />} onClick={() => void navigate({ to: '/cardiology/studies/$studyId', params: { studyId: study.id } })}>
                        Open study and report
                      </MenuItem>
                      {others.map((action) => (
                        <MenuItem key={action} onClick={() => actions.run(action, ref)}>
                          {cardiologyActionLabel[action]}
                        </MenuItem>
                      ))}
                    </MenuList>
                  </MenuPopover>
                </Menu>
              </span>
            );
          }}
          emptyTitle={search.search ? 'No study matches' : 'No cardiology studies for these dates'}
          emptyDescription="ECG, echo and stress test orders from doctors appear here as soon as they are placed."
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
