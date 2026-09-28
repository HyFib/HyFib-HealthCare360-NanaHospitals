import { Badge, Button, Caption1, Checkbox, Field, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import { PersonAddRegular } from '@fluentui/react-icons';
import { formatDateOnly, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { hrQueries, type EmployeeFilters } from '@/modules/hr/api';
import { EMPLOYEE_STATUSES, employeeStatusLabel, EMPLOYMENT_TYPES, HR_PERMISSIONS as P, type EmployeeStatus, type EmploymentType } from '@/modules/hr/labels';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { FilterPad } from '@/modules/hr/shared/layout';

const SORTS = ['code', 'name', 'joiningDate', 'department'];

export const Route = createFileRoute('/_app/hr/employees/')({
  validateSearch: (s: Record<string, unknown>): EmployeeFilters => ({
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    designationId: typeof s.designationId === 'string' && s.designationId ? s.designationId : undefined,
    status: EMPLOYEE_STATUSES.includes(s.status as EmployeeStatus) ? (s.status as EmployeeStatus) : undefined,
    employmentType: EMPLOYMENT_TYPES.includes(s.employmentType as EmploymentType) ? (s.employmentType as EmploymentType) : undefined,
    includeSeparated: s.includeSeparated === true ? true : undefined,
    sort: typeof s.sort === 'string' && SORTS.includes(s.sort) ? s.sort : undefined,
    desc: s.desc === true ? true : undefined,
  }),
  component: () => (
    <RequirePermission permission={P.employeesRead} what="the employee list">
      <EmployeesPage />
    </RequirePermission>
  ),
});

function EmployeesPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(P.employeesManage);
  const departments = useDepartments();
  const designations = useQuery(hrQueries.designations(true));
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const employees = useQuery(hrQueries.employees(filters));

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({ search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, filters.search, navigate]);

  const set = (patch: Partial<EmployeeFilters>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Employees"
        subtitle="Staff on the rolls of this hospital. Search by name or employee code."
        actions={
          canManage && (
            <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => void navigate({ to: '/hr/employees/new' })}>
              Add employee
            </Button>
          )
        }
      />
      <Section flush>
        <FilterPad>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name or employee code (NHEMP00042)" autoFocus />
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
            <Field label="Designation">
              <Select value={filters.designationId ?? ''} onChange={(_, d) => set({ designationId: d.value || undefined })}>
                <option value="">All</option>
                {(designations.data ?? []).map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Employment">
              <Select value={filters.employmentType ?? ''} onChange={(_, d) => set({ employmentType: (d.value || undefined) as EmploymentType | undefined })}>
                <option value="">All</option>
                {EMPLOYMENT_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {t}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select value={filters.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as EmployeeStatus | undefined })}>
                <option value="">All current</option>
                {EMPLOYEE_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {employeeStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Checkbox
              checked={Boolean(filters.includeSeparated)}
              onChange={(_, d) => set({ includeSeparated: d.checked ? true : undefined })}
              label="Include separated"
              style={{ alignSelf: 'flex-end' }}
            />
          </FilterBar>
        </FilterPad>
        <DataTable
          ariaLabel="Employees"
          rows={employees.data?.items}
          loading={employees.isLoading}
          error={employees.error}
          onRetry={() => void employees.refetch()}
          getRowId={(e) => e.id}
          onRowClick={(e) => void navigate({ to: '/hr/employees/$employeeId', params: { employeeId: e.id } })}
          sort={filters.sort ? { key: filters.sort, desc: Boolean(filters.desc) } : undefined}
          onSortChange={(sort) => set({ sort: sort.key, desc: sort.desc || undefined })}
          columns={[
            { key: 'code', header: 'Code', width: 130, sortable: true, render: (e) => <Text font="monospace">{e.employeeCode}</Text> },
            {
              key: 'name',
              header: 'Name',
              sortable: true,
              render: (e) => (
                <span>
                  <Text weight="semibold">{e.fullName}</Text>
                  {e.designationName && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {e.designationName}
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'department', header: 'Department', sortable: true, render: (e) => e.departmentName ?? '—' },
            { key: 'type', header: 'Employment', priority: 'low', render: (e) => e.employmentType },
            { key: 'joiningDate', header: 'Joined', sortable: true, priority: 'low', render: (e) => formatDateOnly(e.joiningDate) },
            { key: 'phone', header: 'Mobile', priority: 'low', render: (e) => e.phoneMasked ?? '—' },
            {
              key: 'links',
              header: 'Sign-in',
              priority: 'low',
              width: 90,
              render: (e) => (e.hasUserAccount ? <Badge appearance="tint" color="success">Linked</Badge> : <Badge appearance="tint">None</Badge>),
            },
            { key: 'status', header: 'Status', render: (e) => <StatusBadge status={e.status} label={employeeStatusLabel[e.status]} tone={e.status === 'Separated' ? 'informative' : undefined} /> },
          ]}
          emptyTitle={filters.search ? 'No employee matches' : 'No employees yet'}
          emptyDescription={filters.search ? 'Check the spelling or the code, or include separated staff.' : canManage ? 'Add the first employee to start rostering and payroll.' : undefined}
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={employees.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
