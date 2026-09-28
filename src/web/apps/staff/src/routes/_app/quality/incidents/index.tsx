import { Badge, Button, Caption1, Checkbox, Field, Input, SearchBox, Select, Text, tokens, Tooltip } from '@fluentui/react-components';
import { EyeOffRegular, ShieldErrorRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { isIsoDate } from '@/modules/hr/shared/dates';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { UserName } from '@/modules/hr/shared/UserPicker';
import { qualityQueries } from '@/modules/quality/api';
import { IncidentStatusBadge, SentinelBadge, SeverityBadge } from '@/modules/quality/badges';
import {
  INCIDENT_SEVERITIES,
  INCIDENT_STATUSES,
  INCIDENT_TYPES,
  incidentStatusLabel,
  incidentTypeLabel,
  QUALITY_PERMISSIONS as Q,
  severityLabel,
  type IncidentSeverity,
  type IncidentStatus,
  type IncidentType,
} from '@/modules/quality/labels';

interface IncidentSearch {
  view?: 'all';
  status?: IncidentStatus;
  type?: IncidentType;
  severity?: IncidentSeverity;
  sentinelOnly?: boolean;
  departmentId?: string;
  from?: string;
  to?: string;
  search?: string;
  sort?: string;
  desc?: boolean;
  page?: number;
}

const SORTS = ['occurredAt', 'reportedAt', 'severity', 'number'];

export const Route = createFileRoute('/_app/quality/incidents/')({
  validateSearch: (s: Record<string, unknown>): IncidentSearch => ({
    view: s.view === 'all' ? 'all' : undefined,
    status: INCIDENT_STATUSES.includes(s.status as IncidentStatus) ? (s.status as IncidentStatus) : undefined,
    type: INCIDENT_TYPES.includes(s.type as IncidentType) ? (s.type as IncidentType) : undefined,
    severity: INCIDENT_SEVERITIES.includes(s.severity as IncidentSeverity) ? (s.severity as IncidentSeverity) : undefined,
    sentinelOnly: s.sentinelOnly === true ? true : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    from: isIsoDate(s.from) ? s.from : undefined,
    to: isIsoDate(s.to) ? s.to : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    sort: typeof s.sort === 'string' && SORTS.includes(s.sort) ? s.sort : undefined,
    desc: s.desc === false ? false : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={Q.incidentsManage} what="incident management">
      <IncidentsPage />
    </RequirePermission>
  ),
});

function IncidentsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const departments = useDepartments();
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const openOnly = !search.status && search.view !== 'all';
  const sort = search.sort ?? 'reportedAt';
  const desc = search.desc ?? true;
  const incidents = useQuery(
    qualityQueries.incidents({
      status: search.status,
      type: search.type,
      severity: search.severity,
      sentinelOnly: search.sentinelOnly,
      openOnly: openOnly || undefined,
      departmentId: search.departmentId,
      from: search.from,
      to: search.to,
      search: search.search,
      sort,
      desc,
      page: search.page,
    }),
  );

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.search, navigate]);

  const set = (patch: Partial<IncidentSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Incidents"
        subtitle="Review, grade, investigate and close reported incidents. Sentinel events need a root-cause analysis before closing."
        icon={<ShieldErrorRegular />}
        actions={
          <Button icon={<ShieldErrorRegular />} onClick={() => void navigate({ to: '/quality/report' })}>
            Report an incident
          </Button>
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Report number or words in the description" />
            </Field>
            <Field label="Stage">
              <Select
                value={search.status ?? (search.view === 'all' ? 'all' : 'open')}
                onChange={(_, d) =>
                  set(d.value === 'open' ? { status: undefined, view: undefined } : d.value === 'all' ? { status: undefined, view: 'all' } : { status: d.value as IncidentStatus, view: undefined })
                }
              >
                <option value="open">All open</option>
                {INCIDENT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {incidentStatusLabel[s]}
                  </option>
                ))}
                <option value="all">Everything</option>
              </Select>
            </Field>
            <Field label="Kind">
              <Select value={search.type ?? ''} onChange={(_, d) => set({ type: (d.value || undefined) as IncidentType | undefined })}>
                <option value="">All</option>
                {INCIDENT_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {incidentTypeLabel[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Seriousness">
              <Select value={search.severity ?? ''} onChange={(_, d) => set({ severity: (d.value || undefined) as IncidentSeverity | undefined })}>
                <option value="">All</option>
                {INCIDENT_SEVERITIES.map((s) => (
                  <option key={s} value={s}>
                    {severityLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department">
              <Select value={search.departmentId ?? ''} onChange={(_, d) => set({ departmentId: d.value || undefined })}>
                <option value="">All</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Happened from">
              <Input type="date" value={search.from ?? ''} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="to">
              <Input type="date" value={search.to ?? ''} onChange={(_, d) => set({ to: d.value || undefined })} />
            </Field>
            <Checkbox style={{ alignSelf: 'flex-end' }} checked={Boolean(search.sentinelOnly)} onChange={(_, d) => set({ sentinelOnly: d.checked ? true : undefined })} label="Sentinel events only" />
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Incidents"
          rows={incidents.data?.items}
          loading={incidents.isLoading}
          error={incidents.error}
          onRetry={() => void incidents.refetch()}
          getRowId={(i) => i.id}
          onRowClick={(i) => void navigate({ to: '/quality/incidents/$incidentId', params: { incidentId: i.id } })}
          sort={{ key: sort, desc }}
          onSortChange={(next) => set({ sort: next.key === 'reportedAt' ? undefined : next.key, desc: next.desc ? undefined : false })}
          columns={[
            {
              key: 'number',
              header: 'Report',
              width: 150,
              sortable: true,
              render: (i) => (
                <span style={{ display: 'inline-flex', gap: 4, alignItems: 'center' }}>
                  <Text font="monospace">{i.incidentNumber}</Text>
                  {i.isAnonymous && (
                    <Tooltip content="Reported anonymously" relationship="label">
                      <EyeOffRegular />
                    </Tooltip>
                  )}
                </span>
              ),
            },
            {
              key: 'type',
              header: 'Incident',
              render: (i) => (
                <span>
                  <Text weight="semibold">{incidentTypeLabel[i.type]}</Text> {i.isSentinel && <SentinelBadge />}
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {i.location}
                    {i.departmentName ? ` · ${i.departmentName}` : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'severity', header: 'Seriousness', sortable: true, render: (i) => <SeverityBadge severity={i.severity} /> },
            { key: 'occurredAt', header: 'Happened', sortable: true, priority: 'low', render: (i) => formatDateTime(i.occurredAt) },
            { key: 'reportedAt', header: 'Reported', sortable: true, priority: 'low', render: (i) => formatDateTime(i.reportedAt) },
            { key: 'assigned', header: 'With', priority: 'low', render: (i) => (i.assignedToUserId ? <UserName id={i.assignedToUserId} /> : <Caption1>Not assigned</Caption1>) },
            {
              key: 'status',
              header: 'Stage',
              render: (i) => (
                <span style={{ display: 'inline-flex', gap: 4, alignItems: 'center', flexWrap: 'wrap' }}>
                  <IncidentStatusBadge status={i.status} />
                  {i.openCapas > 0 && (
                    <Badge size="small" appearance="tint" color="warning" icon={<WarningRegular />}>
                      {i.openCapas} open action(s)
                    </Badge>
                  )}
                </span>
              ),
            },
          ]}
          emptyTitle={openOnly ? 'No open incidents' : 'No incidents match'}
          emptyDescription={openOnly ? 'New reports from staff appear here and in your notifications.' : 'Try a wider date range or clear the filters.'}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={incidents.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
