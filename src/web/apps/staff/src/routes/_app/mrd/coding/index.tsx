import { Field, Input, Select, Text } from '@fluentui/react-components';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { mrdQueries, type CodingListParams, type CodingStatus } from '@/modules/mrd/api';

const DATE = /^\d{4}-\d{2}-\d{2}$/;
const STATUSES: CodingStatus[] = ['Pending', 'Coded', 'Verified'];

export const Route = createFileRoute('/_app/mrd/coding/')({
  validateSearch: (s: Record<string, unknown>): CodingListParams => ({
    status: STATUSES.includes(s.status as CodingStatus) ? (s.status as CodingStatus) : undefined,
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: CodingQueuePage,
});

const statusLabel: Record<CodingStatus, string> = { Pending: 'To code', Coded: 'To verify', Verified: 'Verified' };

/** Clinical coding worklist (MRD-02): discharged episodes waiting for ICD-10 coding, then a second coder's check. */
function CodingQueuePage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const codings = useQuery(mrdQueries.codings(filters));
  const set = (patch: Partial<CodingListParams>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });
  const today = todayIso();

  return (
    <Page>
      <PageHeader
        title="Coding queue"
        subtitle="Episodes to code first, then coding waiting for verification by a second coder."
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Status">
              <Select
                value={filters.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as CodingStatus | undefined })}
              >
                <option value="">All (to code first)</option>
                {STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {statusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Episode from">
              <Input
                type="date"
                value={filters.from ?? ''}
                max={filters.to ?? today}
                onChange={(_, d) => set({ from: d.value || undefined })}
              />
            </Field>
            <Field label="Episode to">
              <Input
                type="date"
                value={filters.to ?? ''}
                min={filters.from}
                max={today}
                onChange={(_, d) => set({ to: d.value || undefined })}
              />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Coding queue"
          rows={codings.data?.items}
          loading={codings.isLoading}
          error={codings.error}
          onRetry={() => void codings.refetch()}
          getRowId={(c) => c.id}
          onRowClick={(c) => void navigate({ to: '/mrd/coding/$codingId', params: { codingId: c.id } })}
          size="small"
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (c) => (
                <span>
                  <Text weight="semibold">{c.patientName ?? '—'}</Text> <Text size={200}>· {c.uhid ?? ''}</Text>
                </span>
              ),
            },
            {
              key: 'episode',
              header: 'Episode',
              render: (c) => (c.admissionId ? 'In-patient stay' : 'Out-patient encounter'),
            },
            {
              key: 'date',
              header: 'Episode date',
              width: 130,
              render: (c) => (c.episodeDate ? formatDateOnly(c.episodeDate) : '—'),
            },
            {
              key: 'coded',
              header: 'Coded',
              priority: 'low',
              render: (c) => (c.codedAt ? formatDateTime(c.codedAt) : '—'),
            },
            {
              key: 'verified',
              header: 'Verified',
              priority: 'low',
              render: (c) => (c.verifiedAt ? formatDateTime(c.verifiedAt) : '—'),
            },
            {
              key: 'status',
              header: 'Status',
              width: 120,
              render: (c) => <StatusBadge status={c.status} label={statusLabel[c.status]} />,
            },
          ]}
          emptyTitle="Nothing to code"
          emptyDescription="Episodes are added automatically when a patient is discharged."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={codings.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
