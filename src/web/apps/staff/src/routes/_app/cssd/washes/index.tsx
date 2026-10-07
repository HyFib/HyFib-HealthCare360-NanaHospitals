import { Button, Caption1, Field, SearchBox, Select, Text } from '@fluentui/react-components';
import { AddRegular, WasherRegular } from '@fluentui/react-icons';
import { formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  cssdQueries,
  WASH_STATUS_LABEL,
  WASH_STATUSES,
  washStatusTone,
  type WashCycleStatus,
} from '@/modules/cssd/api';
import { StartWashDialog } from '@/modules/cssd/WashDialogs';

interface WashesSearch {
  status?: WashCycleStatus;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/cssd/washes/')({
  validateSearch: (s: Record<string, unknown>): WashesSearch => ({
    status: WASH_STATUSES.includes(s.status as WashCycleStatus) ? (s.status as WashCycleStatus) : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: WashesPage,
});

/** Washer-disinfector cycles (CSSD-04): trays are washed and disinfected before they are packed and sterilized. */
function WashesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canProcess = usePermission('cssd.process');
  const [starting, setStarting] = useState(false);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const washes = useQuery(cssdQueries.washes({ status: search.status, search: search.q, page: search.page }));

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  return (
    <Page>
      <PageHeader
        title="Wash cycles"
        icon={<WasherRegular />}
        subtitle="Newest first. Trays are washed and disinfected here before they are packed for the sterilizer."
        actions={
          canProcess && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setStarting(true)}>
              New wash cycle
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Find" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Cycle number or washer" />
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) =>
                  void navigate({
                    search: (prev) => ({
                      ...prev,
                      status: (d.value || undefined) as WashCycleStatus | undefined,
                      page: undefined,
                    }),
                    replace: true,
                  })
                }
              >
                <option value="">Any</option>
                {WASH_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {WASH_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Wash cycles"
          rows={washes.data?.items}
          loading={washes.isLoading}
          error={washes.error}
          onRetry={() => void washes.refetch()}
          getRowId={(w) => w.id}
          onRowClick={(w) => void navigate({ to: '/cssd/washes/$washId', params: { washId: w.id } })}
          columns={[
            {
              key: 'cycle',
              header: 'Cycle',
              width: 170,
              render: (w) => (
                <Text font="monospace" weight="semibold">
                  {w.cycleNumber}
                </Text>
              ),
            },
            {
              key: 'washer',
              header: 'Washer',
              render: (w) => (
                <span>
                  {w.washerName}
                  <Caption1 block>
                    {w.trays} {w.trays === 1 ? 'tray' : 'trays'}
                    {w.program ? ` · ${w.program}` : ''}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'started',
              header: 'Started',
              width: 170,
              priority: 'low',
              render: (w) => formatDateTime(w.startedAt),
            },
            { key: 'a0', header: 'A0', width: 90, align: 'end', priority: 'low', render: (w) => w.a0 ?? '—' },
            {
              key: 'status',
              header: 'Status',
              width: 200,
              render: (w) => (
                <span>
                  <StatusBadge status={w.status} label={WASH_STATUS_LABEL[w.status]} tone={washStatusTone(w.status)} />
                  {w.failureReason && <Caption1 block>{w.failureReason}</Caption1>}
                </span>
              ),
            },
          ]}
          emptyTitle="No wash cycles match"
          emptyDescription={canProcess ? 'Start a wash cycle when the trays go into the washer.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={washes.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {starting && (
        <StartWashDialog
          onClose={() => setStarting(false)}
          onStarted={(wash) => {
            setStarting(false);
            void navigate({ to: '/cssd/washes/$washId', params: { washId: wash.id } });
          }}
        />
      )}
    </Page>
  );
}
