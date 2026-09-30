import {
  Badge,
  Button,
  Caption1,
  Field,
  SearchBox,
  Select,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, CallRegular } from '@fluentui/react-icons';
import { formatDateOnly, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { crmQueries, type FollowUpFilters, type FollowUpListItem } from '@/modules/crm/api';
import { FollowUpCreateDialog } from '@/modules/crm/FollowUpCreateDialog';
import { FollowUpDrawer } from '@/modules/crm/FollowUpDrawer';
import {
  CRM_PERMISSIONS as C,
  FOLLOW_UP_PRIORITIES,
  FOLLOW_UP_STATUSES,
  FOLLOW_UP_TYPES,
  followUpOutcomeLabel,
  followUpStatusLabel,
  followUpTypeLabel,
  type FollowUpPriority,
  type FollowUpStatus,
  type FollowUpType,
} from '@/modules/crm/labels';
import { MAX_UNREACHABLE_ATTEMPTS } from '@/modules/crm/rules';
import { dueLabel, daysFromToday } from '@/modules/hr/shared/dates';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { UserName } from '@/modules/hr/shared/UserPicker';

const VIEWS = ['today', 'overdue', 'mine', 'upcoming', 'all'] as const;
type View = (typeof VIEWS)[number];

interface FollowUpSearch {
  view?: View;
  type?: FollowUpType;
  priority?: FollowUpPriority;
  status?: FollowUpStatus;
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/crm/follow-ups')({
  validateSearch: (s: Record<string, unknown>): FollowUpSearch => ({
    view: VIEWS.includes(s.view as View) && s.view !== 'today' ? (s.view as View) : undefined,
    type: FOLLOW_UP_TYPES.includes(s.type as FollowUpType) ? (s.type as FollowUpType) : undefined,
    priority: FOLLOW_UP_PRIORITIES.includes(s.priority as FollowUpPriority)
      ? (s.priority as FollowUpPriority)
      : undefined,
    status: FOLLOW_UP_STATUSES.includes(s.status as FollowUpStatus) ? (s.status as FollowUpStatus) : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={C.followUpsManage} what="the follow-up call list">
      <FollowUpsPage />
    </RequirePermission>
  ),
});

function filtersFor(view: View, today: string): FollowUpFilters {
  switch (view) {
    case 'overdue':
      return { overdueOnly: true };
    case 'mine':
      return { mine: true, dueTo: today };
    case 'upcoming':
      return { dueFrom: today };
    case 'all':
      return { includeClosed: true };
    default:
      return { dueTo: today };
  }
}

function Due({ f }: { f: FollowUpListItem }) {
  const days = daysFromToday(f.dueDate);
  const closed = f.status === 'Completed' || f.status === 'Cancelled';
  return (
    <span>
      <Text
        weight={f.isOverdue ? 'semibold' : 'regular'}
        style={{ color: f.isOverdue ? tokens.colorPaletteRedForeground1 : undefined }}
      >
        {closed ? formatDateOnly(f.dueDate) : dueLabel(days)}
      </Text>
      {!closed && (
        <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
          {formatDateOnly(f.dueDate)}
        </Caption1>
      )}
    </span>
  );
}

function FollowUpsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [openId, setOpenId] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const view: View = search.view ?? 'today';
  const followUps = useQuery(
    crmQueries.followUps({
      ...filtersFor(view, todayIso()),
      type: search.type,
      priority: search.priority,
      status: view === 'all' ? search.status : undefined,
      search: search.search,
      page: search.page,
    }),
  );

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const set = (patch: Partial<FollowUpSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Follow-up calls"
        subtitle="Call each patient, record what happened and book the next step. Calls after discharge are added automatically."
        icon={<CallRegular />}
        breadcrumbs={[{ label: 'Patient relations', onClick: () => void navigate({ to: '/crm' }) }]}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setAdding(true)}>
            Add follow-up call
          </Button>
        }
      />
      <TabList
        selectedValue={view}
        onTabSelect={(_, d) => set({ view: d.value === 'today' ? undefined : (d.value as View), status: undefined })}
        style={{ marginBottom: 8 }}
      >
        <Tab value="today">Due today</Tab>
        <Tab value="overdue">Overdue</Tab>
        <Tab value="mine">Mine</Tab>
        <Tab value="upcoming">Upcoming</Tab>
        <Tab value="all">All</Tab>
      </TabList>
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Patient" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name or UHID" />
            </Field>
            <Field label="Reason">
              <Select
                value={search.type ?? ''}
                onChange={(_, d) => set({ type: (d.value || undefined) as FollowUpType | undefined })}
              >
                <option value="">All</option>
                {FOLLOW_UP_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {followUpTypeLabel[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Priority">
              <Select
                value={search.priority ?? ''}
                onChange={(_, d) => set({ priority: (d.value || undefined) as FollowUpPriority | undefined })}
              >
                <option value="">Any</option>
                {FOLLOW_UP_PRIORITIES.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </Select>
            </Field>
            {view === 'all' && (
              <Field label="Stage">
                <Select
                  value={search.status ?? ''}
                  onChange={(_, d) => set({ status: (d.value || undefined) as FollowUpStatus | undefined })}
                >
                  <option value="">Any</option>
                  {FOLLOW_UP_STATUSES.map((s) => (
                    <option key={s} value={s}>
                      {followUpStatusLabel[s]}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Follow-up calls"
          rows={followUps.data?.items}
          loading={followUps.isLoading}
          error={followUps.error}
          onRetry={() => void followUps.refetch()}
          getRowId={(f) => f.id}
          onRowClick={(f) => setOpenId(f.id)}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (f) => (
                <span>
                  <Text weight="semibold">{f.patientName}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {f.uhid}
                    {f.phoneMasked ? ` · ${f.phoneMasked}` : ''}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'type',
              header: 'Reason',
              render: (f) => (
                <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                  {followUpTypeLabel[f.type]}
                  {f.priority === 'High' && (
                    <Badge size="small" appearance="tint" color="danger">
                      High
                    </Badge>
                  )}
                </span>
              ),
            },
            { key: 'due', header: 'Call on', render: (f) => <Due f={f} /> },
            {
              key: 'attempts',
              header: 'Attempts',
              align: 'center',
              priority: 'low',
              render: (f) => (f.attempts === 0 ? '—' : `${f.attempts} of ${MAX_UNREACHABLE_ATTEMPTS}`),
            },
            {
              key: 'assignee',
              header: 'Caller',
              priority: 'low',
              render: (f) => (f.assigneeUserId ? <UserName id={f.assigneeUserId} /> : 'Anyone'),
            },
            {
              key: 'status',
              header: 'Stage',
              render: (f) => (
                <span>
                  <StatusBadge
                    status={f.status}
                    tone={
                      f.status === 'Completed'
                        ? 'success'
                        : f.status === 'Cancelled'
                          ? 'informative'
                          : f.isOverdue
                            ? 'danger'
                            : 'warning'
                    }
                    label={followUpStatusLabel[f.status]}
                  />
                  {f.outcome && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {followUpOutcomeLabel[f.outcome]}
                    </Caption1>
                  )}
                </span>
              ),
            },
          ]}
          emptyTitle={
            view === 'today'
              ? 'No calls due today'
              : view === 'overdue'
                ? 'Nothing overdue'
                : 'No follow-up calls match'
          }
          emptyDescription={
            view === 'today'
              ? 'Well done. Upcoming calls are in the Upcoming tab.'
              : 'Try another tab or clear the filters.'
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={followUps.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>

      <FollowUpDrawer followUpId={openId} onClose={() => setOpenId(null)} />
      <FollowUpCreateDialog open={adding} onClose={() => setAdding(false)} />
    </Page>
  );
}
