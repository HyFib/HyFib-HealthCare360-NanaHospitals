import {
  Button,
  Caption1,
  Field,
  makeStyles,
  SearchBox,
  Select,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { useDebouncedValue, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { searchFlag, searchId, searchOneOf, searchText } from '@/modules/lab/common/search';
import { MASTERS_MANAGE, mastersQueries, type DepartmentKind } from '@/modules/masters/api';
import { DepartmentEditorDrawer } from '@/modules/masters/DepartmentEditor';
import { matchesSearch, nextDisplayOrder } from '@/modules/masters/forms';
import { DEPARTMENT_KIND_LABELS, DEPARTMENT_KINDS } from '@/modules/masters/labels';
import { InUseBadge, ListToolbar } from '@/modules/masters/parts';

interface DepartmentsSearch {
  q?: string;
  kind?: DepartmentKind;
  inactive?: true;
  edit?: string;
}

export const Route = createFileRoute('/_app/setup/departments')({
  validateSearch: (s: Record<string, unknown>): DepartmentsSearch => ({
    q: searchText(s.q),
    kind: searchOneOf(s.kind, DEPARTMENT_KINDS),
    inactive: searchFlag(s.inactive),
    edit: s.edit === 'new' ? 'new' : searchId(s.edit),
  }),
  component: () => (
    <RequirePermission permission={MASTERS_MANAGE} what="the departments">
      <DepartmentsPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The departments of the signed-in facility, in the order lists show them. */
function DepartmentsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'this facility';
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const departments = useQuery(mastersQueries.departments(search.inactive === true));
  const specialties = useQuery(mastersQueries.specialties());
  const set = (patch: Partial<DepartmentsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const specialtyName = new Map((specialties.data ?? []).map((s) => [s.id, s.name]));
  const rows = departments.data?.filter(
    (d) => (!search.kind || d.kind === search.kind) && matchesSearch([d.name, d.code], term),
  );

  return (
    <Page>
      <PageHeader
        title="Departments"
        subtitle={`The departments of ${facilityName}: what patients are registered and queued to, and where charges are booked.`}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ edit: 'new' })}>
            Add department
          </Button>
        }
      />
      <Section flush>
        <ListToolbar>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, data) => setTerm(data.value)} placeholder="Name or code" />
            </Field>
            <Field label="Kind">
              <Select
                value={search.kind ?? ''}
                onChange={(_, d) => set({ kind: (d.value || undefined) as DepartmentKind | undefined })}
              >
                <option value="">All kinds</option>
                {DEPARTMENT_KINDS.map((kind) => (
                  <option key={kind} value={kind}>
                    {DEPARTMENT_KIND_LABELS[kind]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={search.inactive === true}
              onChange={(_, d) => set({ inactive: d.checked || undefined })}
              label="Show those not in use"
            />
          </FilterBar>
        </ListToolbar>
        <DataTable
          ariaLabel="Departments"
          rows={rows}
          loading={departments.isLoading}
          error={departments.error}
          onRetry={() => void departments.refetch()}
          getRowId={(d) => d.id}
          onRowClick={(d) => set({ edit: d.id })}
          columns={[
            {
              key: 'name',
              header: 'Department',
              render: (d) => (
                <span className={styles.stack}>
                  <Text weight="semibold">{d.name}</Text>
                  {d.location && <Caption1 className={styles.muted}>{d.location}</Caption1>}
                </span>
              ),
            },
            { key: 'code', header: 'Code', width: 100, render: (d) => <Text font="monospace">{d.code}</Text> },
            { key: 'kind', header: 'Kind', width: 140, priority: 'low', render: (d) => DEPARTMENT_KIND_LABELS[d.kind] },
            {
              key: 'specialty',
              header: 'Speciality',
              priority: 'low',
              render: (d) => (d.specialtyId ? (specialtyName.get(d.specialtyId) ?? '—') : '—'),
            },
            {
              key: 'token',
              header: 'Token',
              width: 90,
              priority: 'low',
              render: (d) => <Text font="monospace">{d.tokenPrefix ?? d.code}</Text>,
            },
            { key: 'status', header: 'Status', width: 120, render: (d) => <InUseBadge isActive={d.isActive} /> },
          ]}
          emptyTitle={term || search.kind ? 'No department matches' : 'No departments yet'}
          emptyDescription={
            term || search.kind
              ? 'Clear the search or choose another kind.'
              : 'Add the departments patients are registered and queued to.'
          }
          emptyAction={
            <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ edit: 'new' })}>
              Add department
            </Button>
          }
        />
      </Section>
      {search.edit && (
        <DepartmentEditorDrawer
          key={search.edit}
          departmentId={search.edit === 'new' ? null : search.edit}
          nextOrder={nextDisplayOrder(departments.data ?? [])}
          onClose={() => set({ edit: undefined })}
        />
      )}
    </Page>
  );
}
