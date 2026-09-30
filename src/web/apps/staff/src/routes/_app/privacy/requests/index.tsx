import {
  Button,
  Caption1,
  Field,
  makeStyles,
  SearchBox,
  Select,
  Tab,
  TabList,
  Tag,
  TagGroup,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ShieldPersonRegular } from '@fluentui/react-icons';
import { formatDate, useDebouncedValue, useRealtimeEvent, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, Navigate, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { FilterPad } from '@/modules/hr/shared/layout';
import { patientQueries } from '@/modules/patients/api';
import { NoAccess } from '@/modules/hr/shared/NoAccess';
import { privacyKeys, privacyQueries, REQUEST_PAGE_SIZE, type DataRequestListItem } from '@/modules/privacy/api';
import { DeadlineWords, IdentityState, RequestStatusBadge } from '@/modules/privacy/badges';
import {
  PRIVACY_PERMISSIONS as P,
  REQUEST_STATUSES,
  REQUEST_TYPES,
  requestChannelLabel,
  requesterRelationLabel,
  requestStatusLabel,
  requestTypeLabel,
  type DataRequestStatus,
  type DataRequestType,
} from '@/modules/privacy/labels';
import { RequestIntakeDialog } from '@/modules/privacy/RequestIntakeDialog';
import { closedText, isOpenStatus, isRecordId, replyByText } from '@/modules/privacy/rules';

const VIEWS = ['open', 'overdue', 'mine', 'all'] as const;
type View = (typeof VIEWS)[number];

interface RequestsSearch {
  view?: View;
  type?: DataRequestType;
  status?: DataRequestStatus;
  /** Only this patient's requests (from the patient record); also chosen in "Log a request". */
  patientId?: string;
  search?: string;
  page?: number;
  /** Opens "Log a request" (the quick action lands here). */
  new?: boolean;
}

export const Route = createFileRoute('/_app/privacy/requests/')({
  validateSearch: (s: Record<string, unknown>): RequestsSearch => ({
    view: VIEWS.includes(s.view as View) && s.view !== 'open' ? (s.view as View) : undefined,
    type: REQUEST_TYPES.includes(s.type as DataRequestType) ? (s.type as DataRequestType) : undefined,
    status: REQUEST_STATUSES.includes(s.status as DataRequestStatus) ? (s.status as DataRequestStatus) : undefined,
    patientId: isRecordId(s.patientId) ? s.patientId : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    new: s.new === true ? true : undefined,
  }),
  component: RequestsRoute,
});

/** The grievance officer's queue. Front-desk staff, who may only log requests, go straight to the intake. */
function RequestsRoute() {
  const canRead = usePermission(P.requestsRead);
  const canLog = usePermission(P.requestsLog);
  if (canRead) {
    return <RequestsPage />;
  }
  return canLog ? <DeskRedirect /> : <NoAccess what="privacy requests" />;
}

/** Log-only users land on the intake page, keeping the patient when one was chosen. */
function DeskRedirect() {
  const { patientId } = Route.useSearch();
  return <Navigate to="/privacy/requests/new" search={{ patientId }} replace />;
}

const useStyles = makeStyles({
  patientRow: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap', marginBottom: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function ReplyBy({ r, now }: { r: DataRequestListItem; now: number }) {
  if (!isOpenStatus(r.status)) {
    return (
      <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>
        {closedText(r.closedAt, now) ?? `Was due ${formatDate(r.dueAt)}`}
      </Caption1>
    );
  }
  const due = replyByText(r.dueAt, now);
  return <DeadlineWords text={due.text} overdue={due.overdue} warn={due.soon} />;
}

function RequestsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const canLog = usePermission(P.requestsLog);
  const canReadPatients = usePermission('patients.read');
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const view: View = search.view ?? 'open';
  const requests = useQuery(
    privacyQueries.requests({
      view,
      type: search.type,
      status: search.status,
      patientId: search.patientId,
      search: search.search,
      page: search.page,
    }),
  );
  const overview = useQuery(privacyQueries.overview());
  // The patient the list is filtered to, by name; usually already loaded by the patient record it came from.
  const patient = useQuery({
    ...patientQueries.detail(search.patientId ?? ''),
    enabled: Boolean(search.patientId) && canReadPatients,
    staleTime: 5 * 60_000,
    retry: false,
  });
  const firstRow = requests.data?.items[0];
  const patientName = patient.data
    ? `${patient.data.fullName} · ${patient.data.uhid}`
    : firstRow?.patientId === search.patientId && firstRow
      ? `${firstRow.principalName}${firstRow.uhid ? ` · ${firstRow.uhid}` : ''}`
      : 'the chosen patient';
  // Deadlines are worked out against the time the list was fetched, so the words stay stable between refreshes.
  const now = requests.dataUpdatedAt || 0;

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: privacyKeys.requests });
    void queryClient.invalidateQueries({ queryKey: privacyKeys.overview });
  };
  const topic = facilityId ? `facility:${facilityId}` : null;
  useRealtimeEvent(topic, 'privacy.request.created', refresh);
  useRealtimeEvent(topic, 'privacy.request.updated', refresh);

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const set = (patch: Partial<RequestsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const openNew = (open: boolean) =>
    void navigate({ search: (prev) => ({ ...prev, new: open ? true : undefined }), replace: true });

  // The counts are for the whole facility, so they are left off while the list shows one patient.
  const o = search.patientId ? undefined : overview.data;
  const count = (value: number | undefined) => (value === undefined ? '' : ` (${value.toLocaleString('en-IN')})`);

  return (
    <Page>
      <PageHeader
        title="Privacy requests"
        subtitle="Requests from patients about their personal data under the DPDP Act: access, correction, erasure and grievances. Answer each by its reply-by date."
        icon={<ShieldPersonRegular />}
        actions={
          canLog && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => openNew(true)}>
              Log a request
            </Button>
          )
        }
      />
      <div>
        <TabList
          selectedValue={view}
          onTabSelect={(_, d) => set({ view: d.value === 'open' ? undefined : (d.value as View), status: undefined })}
        >
          <Tab value="open">Open{count(o?.open)}</Tab>
          <Tab value="overdue">Overdue{count(o?.overdue)}</Tab>
          <Tab value="mine">Mine</Tab>
          <Tab value="all">All</Tab>
        </TabList>
        {o && (o.dueThisWeek > 0 || o.awaitingIdentityCheck > 0) && (
          <Caption1 block style={{ color: tokens.colorNeutralForeground3, padding: '4px 12px 0' }}>
            {o.dueThisWeek.toLocaleString('en-IN')} due in the next 7 days ·{' '}
            {o.awaitingIdentityCheck.toLocaleString('en-IN')} waiting for an identity check
          </Caption1>
        )}
      </div>
      <Section flush>
        <FilterPad>
          {search.patientId && (
            <div className={styles.patientRow}>
              <Text className={styles.muted}>Showing requests</Text>
              <TagGroup aria-label="Patient filter" onDismiss={() => set({ patientId: undefined })}>
                <Tag
                  value="patient"
                  dismissible
                  dismissIcon={{ 'aria-label': 'Show every patient' }}
                  shape="circular"
                  size="small"
                >
                  For {patientName}
                </Tag>
              </TagGroup>
            </div>
          )}
          <FilterBar>
            <Field label="Request number" style={{ flex: '1 1 220px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Full number or its last digits"
              />
            </Field>
            <Field label="Type">
              <Select
                value={search.type ?? ''}
                onChange={(_, d) => set({ type: (d.value || undefined) as DataRequestType | undefined })}
              >
                <option value="">Any</option>
                {REQUEST_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {requestTypeLabel[t]}
                  </option>
                ))}
              </Select>
            </Field>
            {view === 'all' && (
              <Field label="Status">
                <Select
                  value={search.status ?? ''}
                  onChange={(_, d) => set({ status: (d.value || undefined) as DataRequestStatus | undefined })}
                >
                  <option value="">Any</option>
                  {REQUEST_STATUSES.map((s) => (
                    <option key={s} value={s}>
                      {requestStatusLabel[s]}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Privacy requests"
          rows={requests.data?.items}
          loading={requests.isLoading}
          error={requests.error}
          onRetry={() => void requests.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/privacy/requests/$requestId', params: { requestId: r.id } })}
          columns={[
            {
              key: 'number',
              header: 'Number',
              width: 170,
              render: (r) => (
                <span>
                  <Text font="monospace">{r.requestNumber}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {requestChannelLabel[r.channel]}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'type',
              header: 'Type',
              width: 150,
              render: (r) => (
                <span>
                  <Text weight="semibold">{requestTypeLabel[r.type]}</Text>
                  {r.requestedBy !== 'Self' && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      By {requesterRelationLabel[r.requestedBy].toLowerCase()}
                    </Caption1>
                  )}
                </span>
              ),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <span>
                  <Text>{r.principalName}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {r.patientId ? (r.uhid ?? 'Registered patient') : 'Not a registered patient'}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'received',
              header: 'Received',
              width: 120,
              priority: 'low',
              render: (r) => formatDate(r.receivedAt),
            },
            { key: 'due', header: 'Reply by', render: (r) => <ReplyBy r={r} now={now} /> },
            {
              key: 'identity',
              header: 'Identity',
              width: 110,
              render: (r) => <IdentityState verified={r.identityVerified} type={r.type} />,
            },
            {
              key: 'owner',
              header: 'Owner',
              priority: 'low',
              render: (r) =>
                r.assignedToName ?? <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>Not assigned</Caption1>,
            },
            {
              key: 'status',
              header: 'Status',
              render: (r) => <RequestStatusBadge status={r.status} isOverdue={r.isOverdue} />,
            },
          ]}
          emptyTitle={
            search.patientId
              ? view === 'all'
                ? 'No requests for this patient'
                : view === 'overdue'
                  ? 'Nothing is overdue for this patient'
                  : 'No open requests for this patient'
              : view === 'overdue'
                ? 'Nothing is overdue'
                : view === 'mine'
                  ? 'Nothing open is assigned to you'
                  : view === 'open'
                    ? 'No open requests'
                    : 'No requests match'
          }
          emptyDescription={
            search.patientId && view !== 'all'
              ? 'Try the All tab for their closed requests, or remove the patient filter.'
              : view === 'overdue'
                ? 'Every open request is within its reply-by date.'
                : view === 'mine'
                  ? 'Requests you start, or that are assigned to you, appear here.'
                  : view === 'open'
                    ? 'Requests from the patient portal, and those logged at the desk, appear here straight away.'
                    : 'Try another type or status, or clear the search.'
          }
          page={search.page ?? 1}
          pageSize={REQUEST_PAGE_SIZE}
          totalCount={requests.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>

      <RequestIntakeDialog
        open={Boolean(search.new) && canLog}
        patientId={search.patientId}
        onClose={() => openNew(false)}
        onCreated={(created) => {
          notify.success(
            `Request ${created.requestNumber} logged`,
            `Reply by ${formatDate(created.dueAt)}. The grievance officer has been alerted.`,
          );
          void navigate({ to: '/privacy/requests/$requestId', params: { requestId: created.id } });
        }}
      />
    </Page>
  );
}
