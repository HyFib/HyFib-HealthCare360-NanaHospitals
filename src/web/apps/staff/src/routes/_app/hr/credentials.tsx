import { Button, Caption1, Field, SearchBox, Select, Text, tokens, Tooltip } from '@fluentui/react-components';
import { CertificateRegular, EditRegular } from '@fluentui/react-icons';
import { formatDateOnly, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { hrQueries, type CredentialDto } from '@/modules/hr/api';
import { CredentialDialog } from '@/modules/hr/CredentialDialog';
import { CREDENTIAL_TYPES, CREDENTIAL_VIEWERS, credentialExpiry, credentialTypeLabel, HR_PERMISSIONS as P, type CredentialType } from '@/modules/hr/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { FilterPad } from '@/modules/hr/shared/layout';

const WINDOWS = ['7', '30', '60', '90', 'all'] as const;
type Window = (typeof WINDOWS)[number];

interface CredentialSearch {
  within?: Window;
  type?: CredentialType;
  departmentId?: string;
  search?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/hr/credentials')({
  validateSearch: (s: Record<string, unknown>): CredentialSearch => ({
    within: WINDOWS.includes(String(s.within) as Window) && String(s.within) !== '60' ? (String(s.within) as Window) : undefined,
    type: CREDENTIAL_TYPES.includes(s.type as CredentialType) ? (s.type as CredentialType) : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: () => (
    <RequirePermission permission={CREDENTIAL_VIEWERS} what="staff credentials">
      <CredentialsPage />
    </RequirePermission>
  ),
});

function CredentialsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(P.credentialsManage);
  const departments = useDepartments();
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [editing, setEditing] = useState<CredentialDto | null>(null);
  const within = filters.within ?? '60';
  const credentials = useQuery(
    hrQueries.credentials({
      expiringWithinDays: within === 'all' ? undefined : Number(within),
      type: filters.type,
      departmentId: filters.departmentId,
      search: filters.search,
      page: filters.page,
    }),
  );

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, filters.search, navigate]);

  const set = (patch: Partial<CredentialSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Credentials and licences"
        subtitle="Council registrations and life-support certificates, soonest expiry first. Reminders go out 30 and 7 days before and on the day."
        icon={<CertificateRegular />}
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Employee name, code or certificate number" />
            </Field>
            <Field label="Expiring">
              <Select value={within} onChange={(_, d) => set({ within: d.value === '60' ? undefined : (d.value as Window) })}>
                <option value="7">Within 7 days (and expired)</option>
                <option value="30">Within 30 days (and expired)</option>
                <option value="60">Within 60 days (and expired)</option>
                <option value="90">Within 90 days (and expired)</option>
                <option value="all">All credentials</option>
              </Select>
            </Field>
            <Field label="Type">
              <Select value={filters.type ?? ''} onChange={(_, d) => set({ type: (d.value || undefined) as CredentialType | undefined })}>
                <option value="">All</option>
                {CREDENTIAL_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {credentialTypeLabel[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department">
              <Select value={filters.departmentId ?? ''} onChange={(_, d) => set({ departmentId: d.value || undefined })}>
                <option value="">All</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Credentials"
          rows={credentials.data?.items}
          loading={credentials.isLoading}
          error={credentials.error}
          onRetry={() => void credentials.refetch()}
          getRowId={(c) => c.id}
          onRowClick={(c) => void navigate({ to: '/hr/employees/$employeeId', params: { employeeId: c.employeeId }, search: { tab: 'credentials' } })}
          columns={[
            {
              key: 'employee',
              header: 'Employee',
              render: (c) => (
                <span>
                  <Text weight="semibold">{c.employeeName}</Text> <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>{c.employeeCode}</Caption1>
                </span>
              ),
            },
            { key: 'credential', header: 'Credential', render: (c) => `${credentialTypeLabel[c.type]} · ${c.name}` },
            { key: 'number', header: 'Number', priority: 'low', render: (c) => c.number },
            { key: 'expires', header: 'Valid until', render: (c) => (c.expiresOn ? formatDateOnly(c.expiresOn) : 'Lifetime') },
            {
              key: 'status',
              header: 'Status',
              render: (c) => {
                const expiry = credentialExpiry(c.stage, c.daysToExpiry);
                return <StatusBadge status={c.stage} tone={expiry.tone} label={expiry.label} />;
              },
            },
          ]}
          rowActions={
            canManage
              ? (c) => (
                  <Tooltip content="Edit, for example after renewal" relationship="label">
                    <Button size="small" appearance="subtle" icon={<EditRegular />} onClick={() => setEditing(c)} />
                  </Tooltip>
                )
              : undefined
          }
          emptyTitle={within === 'all' ? 'No credentials recorded' : 'Nothing expiring in this window'}
          emptyDescription="Credentials are added on each employee's profile."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={credentials.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
      {editing && (
        <CredentialDialog open onClose={() => setEditing(null)} employeeId={editing.employeeId} employeeName={editing.employeeName} credential={editing} />
      )}
    </Page>
  );
}
