import {
  Button,
  Caption1,
  Field,
  Input,
  SearchBox,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ChatMultipleRegular } from '@fluentui/react-icons';
import { formatDateTime, useDebouncedValue, useRealtimeEvent, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { feedbackKeys, feedbackQueries, type ComplaintListItem } from '@/modules/feedback/api';
import { ComplaintStatusBadge, PriorityBadge } from '@/modules/feedback/badges';
import { ComplaintDialog } from '@/modules/feedback/ComplaintDialog';
import {
  COMPLAINT_PRIORITIES,
  COMPLAINT_SOURCES,
  COMPLAINT_STATUSES,
  complaintSourceLabel,
  complaintStatusLabel,
  categoryLabel,
  FEEDBACK_PERMISSIONS as F,
  type ComplaintPriority,
  type ComplaintSource,
  type ComplaintStatus,
} from '@/modules/feedback/labels';
import { deadlineText, turnaroundText } from '@/modules/feedback/rules';
import { isIsoDate } from '@/modules/hr/shared/dates';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { UserName } from '@/modules/hr/shared/UserPicker';

const VIEWS = ['open', 'overdue', 'mine', 'all'] as const;
type View = (typeof VIEWS)[number];

interface ComplaintsSearch {
  view?: View;
  status?: ComplaintStatus;
  priority?: ComplaintPriority;
  source?: ComplaintSource;
  departmentId?: string;
  from?: string;
  to?: string;
  search?: string;
  page?: number;
  /** Opens "Record a complaint" (used by the quick action). */
  new?: boolean;
}

export const Route = createFileRoute('/_app/feedback/complaints/')({
  validateSearch: (s: Record<string, unknown>): ComplaintsSearch => ({
    view: VIEWS.includes(s.view as View) && s.view !== 'open' ? (s.view as View) : undefined,
    status: COMPLAINT_STATUSES.includes(s.status as ComplaintStatus) ? (s.status as ComplaintStatus) : undefined,
    priority: COMPLAINT_PRIORITIES.includes(s.priority as ComplaintPriority)
      ? (s.priority as ComplaintPriority)
      : undefined,
    source: COMPLAINT_SOURCES.includes(s.source as ComplaintSource) ? (s.source as ComplaintSource) : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    new: s.new === true ? true : undefined,
  }),
  component: () => (
    <RequirePermission permission={F.complaintsManage} what="complaint handling">
      <ComplaintsPage />
    </RequirePermission>
  ),
});

function Deadline({ c, now }: { c: ComplaintListItem; now: number }) {
  if (c.status === 'Resolved' || c.status === 'Closed') {
    return <Caption1>Resolved in {turnaroundText(c.turnaroundHours)}</Caption1>;
  }
  const due = deadlineText(c.dueAt, now);
  return (
    <span>
      <Text weight="semibold" style={{ color: due.overdue ? tokens.colorPaletteRedForeground1 : undefined }}>
        {due.text}
      </Text>
      <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
        by {formatDateTime(c.dueAt)}
      </Caption1>
    </span>
  );
}

function ComplaintsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const departments = useDepartments();
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const view: View = search.view ?? 'open';
  const complaints = useQuery(
    feedbackQueries.complaints({
      status: search.status,
      priority: search.priority,
      source: search.source,
      departmentId: search.departmentId,
      openOnly: view === 'open' || undefined,
      overdueOnly: view === 'overdue' || undefined,
      mine: view === 'mine' || undefined,
      from: search.from,
      to: search.to,
      search: search.search,
      page: search.page,
    }),
  );
  const departmentName = new Map((departments.data ?? []).map((d) => [d.id, d.name]));
  // Deadlines are worked out against the time the list was fetched, so the text stays stable between refreshes.
  const now = complaints.dataUpdatedAt || 0;

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'feedback.complaint.created', () => {
    void queryClient.invalidateQueries({ queryKey: feedbackKeys.complaints });
  });

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const set = (patch: Partial<ComplaintsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const openNew = (open: boolean) =>
    void navigate({ search: (prev) => ({ ...prev, new: open ? true : undefined }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Complaints"
        subtitle="Resolve each complaint within its deadline: 24 hours for high priority, 72 hours otherwise. Low feedback scores are added automatically."
        icon={<ChatMultipleRegular />}
        breadcrumbs={[{ label: 'Feedback analytics', onClick: () => void navigate({ to: '/feedback' }) }]}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => openNew(true)}>
            Record a complaint
          </Button>
        }
      />
      <TabList
        selectedValue={view}
        onTabSelect={(_, d) => set({ view: d.value === 'open' ? undefined : (d.value as View), status: undefined })}
        style={{ marginBottom: 8 }}
      >
        <Tab value="open">Open</Tab>
        <Tab value="overdue">Past the deadline</Tab>
        <Tab value="mine">Mine</Tab>
        <Tab value="all">All</Tab>
      </TabList>
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Complaint number" style={{ flex: '1 1 200px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Full number or its last digits"
              />
            </Field>
            {view === 'all' && (
              <Field label="Stage">
                <Select
                  value={search.status ?? ''}
                  onChange={(_, d) => set({ status: (d.value || undefined) as ComplaintStatus | undefined })}
                >
                  <option value="">Any</option>
                  {COMPLAINT_STATUSES.map((s) => (
                    <option key={s} value={s}>
                      {complaintStatusLabel[s]}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <Field label="Priority">
              <Select
                value={search.priority ?? ''}
                onChange={(_, d) => set({ priority: (d.value || undefined) as ComplaintPriority | undefined })}
              >
                <option value="">Any</option>
                {COMPLAINT_PRIORITIES.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Source">
              <Select
                value={search.source ?? ''}
                onChange={(_, d) => set({ source: (d.value || undefined) as ComplaintSource | undefined })}
              >
                <option value="">Any</option>
                {COMPLAINT_SOURCES.map((s) => (
                  <option key={s} value={s}>
                    {complaintSourceLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department">
              <Select
                value={search.departmentId ?? ''}
                onChange={(_, d) => set({ departmentId: d.value || undefined })}
              >
                <option value="">All</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Raised from">
              <Input type="date" value={search.from ?? ''} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="to">
              <Input type="date" value={search.to ?? ''} onChange={(_, d) => set({ to: d.value || undefined })} />
            </Field>
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Complaints"
          rows={complaints.data?.items}
          loading={complaints.isLoading}
          error={complaints.error}
          onRetry={() => void complaints.refetch()}
          getRowId={(c) => c.id}
          onRowClick={(c) => void navigate({ to: '/feedback/complaints/$complaintId', params: { complaintId: c.id } })}
          columns={[
            {
              key: 'number',
              header: 'Complaint',
              width: 170,
              render: (c) => (
                <span>
                  <Text font="monospace">{c.complaintNumber}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {formatDateTime(c.raisedAt)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'area',
              header: 'About',
              render: (c) => (
                <span>
                  <Text weight="semibold">{categoryLabel(c.category)}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {complaintSourceLabel[c.source]}
                    {c.departmentId ? ` · ${departmentName.get(c.departmentId) ?? 'Other department'}` : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'priority', header: 'Priority', render: (c) => <PriorityBadge priority={c.priority} /> },
            { key: 'deadline', header: 'Deadline', render: (c) => <Deadline c={c} now={now} /> },
            {
              key: 'owner',
              header: 'Owner',
              priority: 'low',
              render: (c) => (c.ownerUserId ? <UserName id={c.ownerUserId} /> : <Caption1>Not assigned</Caption1>),
            },
            {
              key: 'status',
              header: 'Stage',
              render: (c) => (
                <ComplaintStatusBadge status={c.status} isOverdue={c.isOverdue} escalated={c.escalatedAt !== null} />
              ),
            },
          ]}
          emptyTitle={
            view === 'overdue'
              ? 'Nothing past its deadline'
              : view === 'open'
                ? 'No open complaints'
                : 'No complaints match'
          }
          emptyDescription={
            view === 'open'
              ? 'New complaints, including low feedback scores, appear here straight away.'
              : 'Try another tab or clear the filters.'
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={complaints.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>

      <ComplaintDialog
        open={Boolean(search.new)}
        onClose={() => openNew(false)}
        onCreated={(complaintId) => void navigate({ to: '/feedback/complaints/$complaintId', params: { complaintId } })}
      />
    </Page>
  );
}
