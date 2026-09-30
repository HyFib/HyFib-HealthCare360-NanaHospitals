import { Badge, Button, Caption1, Field, SearchBox, Select, Switch, Text } from '@fluentui/react-components';
import { AddRegular, FireRegular } from '@fluentui/react-icons';
import { formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { cssdQueries, LOAD_STATUS_LABEL, LOAD_STATUSES, loadStatusTone, type LoadStatus } from '@/modules/cssd/api';
import { IndicatorBadge } from '@/modules/cssd/IndicatorBadge';
import { CreateLoadDialog } from '@/modules/cssd/LoadDialogs';
import { METHOD_LABEL, METHODS, type SterilizationMethod } from '@/modules/cssd/rules';

interface LoadsSearch {
  status?: LoadStatus;
  method?: SterilizationMethod;
  biologicalPending?: boolean;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/cssd/loads/')({
  validateSearch: (s: Record<string, unknown>): LoadsSearch => ({
    status: LOAD_STATUSES.includes(s.status as LoadStatus) ? (s.status as LoadStatus) : undefined,
    method: METHODS.includes(s.method as SterilizationMethod) ? (s.method as SterilizationMethod) : undefined,
    biologicalPending: s.biologicalPending === true ? true : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: LoadsPage,
});

/** Sterilizer cycles (CSSD-04): load, run, record parameters and indicators, release. */
function LoadsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canProcess = usePermission('cssd.process');
  const [creating, setCreating] = useState(false);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const loads = useQuery(
    cssdQueries.loads({
      status: search.status,
      method: search.method,
      biologicalPending: search.biologicalPending,
      search: search.q,
      page: search.page,
    }),
  );

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const set = (patch: Partial<LoadsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Sterilizer loads"
        icon={<FireRegular />}
        subtitle="Newest first. Open a load to record the cycle, indicators and release."
        actions={
          canProcess && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
              New load
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Find" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Cycle number or sterilizer" />
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as LoadStatus | undefined })}
              >
                <option value="">Any</option>
                {LOAD_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {LOAD_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Method">
              <Select
                value={search.method ?? ''}
                onChange={(_, d) => set({ method: (d.value || undefined) as SterilizationMethod | undefined })}
              >
                <option value="">Any</option>
                {METHODS.map((m) => (
                  <option key={m} value={m}>
                    {METHOD_LABEL[m]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(search.biologicalPending)}
              onChange={(_, d) => set({ biologicalPending: d.checked || undefined })}
              label="Biological indicator to read"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Sterilizer loads"
          rows={loads.data?.items}
          loading={loads.isLoading}
          error={loads.error}
          onRetry={() => void loads.refetch()}
          getRowId={(l) => l.id}
          onRowClick={(l) => void navigate({ to: '/cssd/loads/$loadId', params: { loadId: l.id } })}
          columns={[
            {
              key: 'cycle',
              header: 'Cycle',
              width: 170,
              render: (l) => (
                <span>
                  <Text font="monospace" weight="semibold">
                    {l.cycleNumber}
                  </Text>
                  {l.containsImplants && (
                    <Badge size="small" appearance="outline" color="warning" style={{ marginLeft: 6 }}>
                      Implants
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'sterilizer',
              header: 'Sterilizer',
              render: (l) => (
                <span>
                  {l.sterilizerName}
                  <Caption1 block>
                    {METHOD_LABEL[l.method]} · {l.packs} packs
                  </Caption1>
                </span>
              ),
            },
            { key: 'loaded', header: 'Loaded', width: 170, priority: 'low', render: (l) => formatDateTime(l.loadedAt) },
            {
              key: 'indicators',
              header: 'Indicators',
              priority: 'low',
              render: (l) => (
                <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
                  <IndicatorBadge result={l.chemicalIndicator} label="CI" />
                  <IndicatorBadge result={l.biologicalIndicator} label="BI" />
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 170,
              render: (l) => (
                <span>
                  <StatusBadge status={l.status} label={LOAD_STATUS_LABEL[l.status]} tone={loadStatusTone(l.status)} />
                  {l.failureReason && <Caption1 block>{l.failureReason}</Caption1>}
                </span>
              ),
            },
          ]}
          emptyTitle="No loads match"
          emptyDescription={canProcess ? 'Create a load when the sterilizer is packed.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={loads.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {creating && (
        <CreateLoadDialog
          onClose={() => setCreating(false)}
          onCreated={(load) => {
            setCreating(false);
            void navigate({ to: '/cssd/loads/$loadId', params: { loadId: load.id } });
          }}
        />
      )}
    </Page>
  );
}
