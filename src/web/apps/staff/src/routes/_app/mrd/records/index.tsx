import { Badge, Field, SearchBox, Select, Switch, Text } from '@fluentui/react-components';
import { formatDateOnly, formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { mrdQueries, type MedicalRecordStatus, type RecordListParams } from '@/modules/mrd/api';

const STATUSES: MedicalRecordStatus[] = ['Incomplete', 'Deficient', 'Complete', 'Archived'];

export const Route = createFileRoute('/_app/mrd/records/')({
  validateSearch: (s: Record<string, unknown>): RecordListParams => ({
    status: STATUSES.includes(s.status as MedicalRecordStatus) ? (s.status as MedicalRecordStatus) : undefined,
    overdue: s.overdue === true || s.overdue === 'true' ? true : undefined,
    retentionDue: s.retentionDue === true || s.retentionDue === 'true' ? true : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: ChartsPage,
});

/** MRD chart tracking (MRD-03/05): completeness, deficiencies, archival and retention. */
function ChartsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const records = useQuery(mrdQueries.records(filters));
  const set = (patch: Partial<RecordListParams>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }), replace: true });

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, filters.search, navigate]);

  return (
    <Page>
      <PageHeader title="Patient charts" subtitle="Charts are completed within 7 days of discharge. Deficiencies are assigned to the treating doctor." />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Patient" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="UHID, mobile or name" />
            </Field>
            <Field label="Status">
              <Select value={filters.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as MedicalRecordStatus | undefined })}>
                <option value="">All</option>
                {STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {s}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch checked={filters.overdue ?? false} onChange={(_, d) => set({ overdue: d.checked || undefined })} label="Overdue only" />
            <Switch checked={filters.retentionDue ?? false} onChange={(_, d) => set({ retentionDue: d.checked || undefined })} label="Retention period over" />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Patient charts"
          rows={records.data?.items}
          loading={records.isLoading}
          error={records.error}
          onRetry={() => void records.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/mrd/records/$recordId', params: { recordId: r.id } })}
          size="small"
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName ?? '—'}</Text> <Text size={200}>· {r.uhid ?? ''}</Text>
                  {r.isMlc && (
                    <Badge size="small" color="danger" style={{ marginLeft: 6 }}>
                      MLC
                    </Badge>
                  )}
                </span>
              ),
            },
            { key: 'episode', header: 'Episode', priority: 'low', render: (r) => (r.admissionId ? 'In-patient' : 'Out-patient') },
            { key: 'discharged', header: 'Discharged', priority: 'low', render: (r) => (r.dischargedAt ? formatDateTime(r.dischargedAt) : '—') },
            {
              key: 'due',
              header: 'Due',
              width: 130,
              render: (r) =>
                r.dueDate ? (
                  <span>
                    {formatDateOnly(r.dueDate)}{' '}
                    {r.isOverdue && (
                      <Badge size="small" color="danger">
                        Overdue
                      </Badge>
                    )}
                  </span>
                ) : (
                  '—'
                ),
            },
            { key: 'open', header: 'Open deficiencies', width: 140, align: 'end', render: (r) => (r.openDeficiencies > 0 ? <Text weight="semibold">{r.openDeficiencies}</Text> : '0') },
            { key: 'retain', header: 'Keep until', priority: 'low', render: (r) => (r.status === 'Archived' || r.status === 'Complete' ? (r.retainUntil ? formatDateOnly(r.retainUntil) : 'Permanently') : '—') },
            { key: 'status', header: 'Status', width: 110, render: (r) => <StatusBadge status={r.status} tone={r.status === 'Deficient' ? 'danger' : undefined} /> },
          ]}
          emptyTitle="No charts match"
          emptyDescription="Charts are opened automatically when a patient is admitted."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={records.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
