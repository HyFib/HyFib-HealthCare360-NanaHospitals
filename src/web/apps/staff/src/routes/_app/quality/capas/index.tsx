import { Button, Caption1, Checkbox, Field, SearchBox, Select, Text } from '@fluentui/react-components';
import { AddRegular, ClipboardTaskRegular } from '@fluentui/react-icons';
import { formatDateOnly, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { FilterPad } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { qualityQueries } from '@/modules/quality/api';
import { CapaStatusBadge } from '@/modules/quality/badges';
import { CapaDialog } from '@/modules/quality/CapaDialog';
import {
  CAPA_SOURCES,
  CAPA_STATUSES,
  capaSourceLabel,
  capaStatusLabel,
  QUALITY_PERMISSIONS as Q,
  type CapaSource,
  type CapaStatus,
} from '@/modules/quality/labels';

interface CapaSearch {
  status?: CapaStatus;
  view?: 'all';
  overdueOnly?: boolean;
  source?: CapaSource;
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/quality/capas/')({
  validateSearch: (s: Record<string, unknown>): CapaSearch => ({
    status: CAPA_STATUSES.includes(s.status as CapaStatus) ? (s.status as CapaStatus) : undefined,
    view: s.view === 'all' ? 'all' : undefined,
    overdueOnly: s.overdueOnly === true ? true : undefined,
    source: CAPA_SOURCES.includes(s.source as CapaSource) ? (s.source as CapaSource) : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={Q.capaManage} what="corrective actions">
      <CapasPage />
    </RequirePermission>
  ),
});

function CapasPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [planning, setPlanning] = useState(false);
  const openOnly = !search.status && search.view !== 'all';
  const capas = useQuery(
    qualityQueries.capas({
      status: search.status,
      openOnly: openOnly || undefined,
      overdueOnly: search.overdueOnly,
      source: search.source,
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

  const set = (patch: Partial<CapaSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Corrective and preventive actions"
        subtitle="Every action has an owner and a due date; the quality team checks it worked about 30 days after it is done."
        icon={<ClipboardTaskRegular />}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setPlanning(true)}>
            Plan an action
          </Button>
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Action number or words" />
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? (search.view === 'all' ? 'all' : 'open')}
                onChange={(_, d) =>
                  set(
                    d.value === 'open'
                      ? { status: undefined, view: undefined }
                      : d.value === 'all'
                        ? { status: undefined, view: 'all' }
                        : { status: d.value as CapaStatus, view: undefined },
                  )
                }
              >
                <option value="open">Open and in progress</option>
                {CAPA_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {capaStatusLabel[s]}
                  </option>
                ))}
                <option value="all">All</option>
              </Select>
            </Field>
            <Field label="Arising from">
              <Select
                value={search.source ?? ''}
                onChange={(_, d) => set({ source: (d.value || undefined) as CapaSource | undefined })}
              >
                <option value="">All</option>
                {CAPA_SOURCES.map((s) => (
                  <option key={s} value={s}>
                    {capaSourceLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Checkbox
              style={{ alignSelf: 'flex-end' }}
              checked={Boolean(search.overdueOnly)}
              onChange={(_, d) => set({ overdueOnly: d.checked ? true : undefined })}
              label="Overdue only"
            />
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Corrective and preventive actions"
          rows={capas.data?.items}
          loading={capas.isLoading}
          error={capas.error}
          onRetry={() => void capas.refetch()}
          getRowId={(c) => c.id}
          onRowClick={(c) => void navigate({ to: '/quality/capas/$capaId', params: { capaId: c.id } })}
          columns={[
            {
              key: 'action',
              header: 'Action',
              render: (c) => (
                <span>
                  <Text weight="semibold">{c.action}</Text>
                  <Caption1 block>
                    {c.capaNumber} · {c.type} ·{' '}
                    {c.incidentNumber ? `incident ${c.incidentNumber}` : capaSourceLabel[c.source]}
                  </Caption1>
                </span>
              ),
            },
            { key: 'owner', header: 'Owner', render: (c) => c.ownerName ?? '—' },
            { key: 'due', header: 'Due', width: 120, render: (c) => formatDateOnly(c.dueDate) },
            {
              key: 'check',
              header: 'Effect check',
              priority: 'low',
              render: (c) => (c.effectivenessCheckDue ? formatDateOnly(c.effectivenessCheckDue) : '—'),
            },
            {
              key: 'status',
              header: 'Status',
              render: (c) => <CapaStatusBadge status={c.status} isOverdue={c.isOverdue} />,
            },
          ]}
          emptyTitle={search.overdueOnly ? 'Nothing overdue' : 'No actions match'}
          emptyDescription="Actions are planned from incidents, risks, audits or complaints."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={capas.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      <CapaDialog open={planning} onClose={() => setPlanning(false)} context={{ source: 'Audit' }} />
    </Page>
  );
}
