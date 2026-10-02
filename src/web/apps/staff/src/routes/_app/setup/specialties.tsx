import { Button, Caption1, Field, makeStyles, SearchBox, Switch, Text, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { searchFlag, searchId, searchText } from '@/modules/lab/common/search';
import { MASTERS_MANAGE, mastersQueries } from '@/modules/masters/api';
import { matchesSearch } from '@/modules/masters/forms';
import { InUseBadge, ListToolbar } from '@/modules/masters/parts';
import { SpecialtyEditorDrawer } from '@/modules/masters/SpecialtyEditor';

interface SpecialtiesSearch {
  q?: string;
  inactive?: true;
  edit?: string;
}

export const Route = createFileRoute('/_app/setup/specialties')({
  validateSearch: (s: Record<string, unknown>): SpecialtiesSearch => ({
    q: searchText(s.q),
    inactive: searchFlag(s.inactive),
    edit: s.edit === 'new' ? 'new' : searchId(s.edit),
  }),
  component: () => (
    <RequirePermission permission={MASTERS_MANAGE} what="the specialities">
      <SpecialtiesPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The medical specialities doctors and departments belong to, shared by every facility. */
function SpecialtiesPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const specialties = useQuery(mastersQueries.specialties());
  const set = (patch: Partial<SpecialtiesSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const rows = specialties.data?.filter(
    (s) => (search.inactive === true || s.isActive) && matchesSearch([s.name, s.code, s.description], term),
  );

  return (
    <Page>
      <PageHeader
        title="Specialities"
        subtitle="The medical specialities of doctors and departments. Patients choose one when they book online."
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ edit: 'new' })}>
            Add speciality
          </Button>
        }
      />
      <Section flush>
        <ListToolbar>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, data) => setTerm(data.value)} placeholder="Name or code" />
            </Field>
            <Switch
              checked={search.inactive === true}
              onChange={(_, d) => set({ inactive: d.checked || undefined })}
              label="Show those not in use"
            />
          </FilterBar>
        </ListToolbar>
        <DataTable
          ariaLabel="Specialities"
          rows={rows}
          loading={specialties.isLoading}
          error={specialties.error}
          onRetry={() => void specialties.refetch()}
          getRowId={(s) => s.id}
          onRowClick={(s) => set({ edit: s.id })}
          columns={[
            {
              key: 'name',
              header: 'Speciality',
              render: (s) => (
                <span className={styles.stack}>
                  <Text weight="semibold">{s.name}</Text>
                  {s.description && <Caption1 className={styles.muted}>{s.description}</Caption1>}
                </span>
              ),
            },
            { key: 'code', header: 'Code', width: 140, render: (s) => <Text font="monospace">{s.code}</Text> },
            { key: 'status', header: 'Status', width: 120, render: (s) => <InUseBadge isActive={s.isActive} /> },
          ]}
          emptyTitle={term ? 'No speciality matches' : 'No specialities yet'}
          emptyDescription={term ? 'Clear the search to see them all.' : 'Add the specialities your doctors practise.'}
        />
      </Section>
      {search.edit && (
        <SpecialtyEditorDrawer
          key={search.edit}
          specialtyId={search.edit === 'new' ? null : search.edit}
          onClose={() => set({ edit: undefined })}
        />
      )}
    </Page>
  );
}
