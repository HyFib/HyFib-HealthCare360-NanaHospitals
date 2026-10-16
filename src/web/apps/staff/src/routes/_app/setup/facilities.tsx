import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatPhone } from '@hc360/core';
import { DataTable, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { searchId } from '@/modules/lab/common/search';
import { MASTERS_MANAGE, mastersQueries } from '@/modules/masters/api';
import { FacilityEditorDrawer } from '@/modules/masters/FacilityEditor';
import { InUseBadge } from '@/modules/masters/parts';

interface FacilitiesSearch {
  /** The facility open in the editor, or "new". */
  edit?: string;
}

export const Route = createFileRoute('/_app/setup/facilities')({
  validateSearch: (s: Record<string, unknown>): FacilitiesSearch => ({
    edit: s.edit === 'new' ? 'new' : searchId(s.edit),
  }),
  component: () => (
    <RequirePermission permission={MASTERS_MANAGE} what="the facilities">
      <FacilitiesPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The hospital and its branches: the details every document prints on its letterhead (HMS-03). */
function FacilitiesPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const facilities = useQuery(mastersQueries.facilities());
  const edit = (id: string | undefined) => void navigate({ search: (prev) => ({ ...prev, edit: id }) });

  return (
    <Page>
      <PageHeader
        title="Facilities and letterhead"
        subtitle="The hospital and its branches, with the name, address, registrations and logo that documents print."
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => edit('new')}>
            Add facility
          </Button>
        }
      />
      <Section flush>
        <DataTable
          ariaLabel="Facilities"
          rows={facilities.data}
          loading={facilities.isLoading}
          error={facilities.error}
          onRetry={() => void facilities.refetch()}
          getRowId={(f) => f.id}
          onRowClick={(f) => edit(f.id)}
          columns={[
            {
              key: 'name',
              header: 'Facility',
              render: (f) => (
                <span className={styles.stack}>
                  <Text weight="semibold">{f.name}</Text>
                  {f.legalName && <Caption1 className={styles.muted}>{f.legalName}</Caption1>}
                </span>
              ),
            },
            { key: 'code', header: 'Code', width: 90, render: (f) => <Text font="monospace">{f.code}</Text> },
            { key: 'city', header: 'City', priority: 'low', render: (f) => f.address.city ?? '—' },
            { key: 'phone', header: 'Phone', priority: 'low', render: (f) => formatPhone(f.phone) },
            { key: 'gstin', header: 'GSTIN', priority: 'low', render: (f) => f.gstin ?? '—' },
            { key: 'status', header: 'Status', width: 120, render: (f) => <InUseBadge isActive={f.isActive} /> },
          ]}
          emptyTitle="No facilities yet"
          emptyDescription="Add the hospital's details; documents print them on the letterhead."
          emptyAction={
            <Button appearance="primary" icon={<AddRegular />} onClick={() => edit('new')}>
              Add facility
            </Button>
          }
        />
      </Section>
      {search.edit && (
        <FacilityEditorDrawer
          key={search.edit}
          facilityId={search.edit === 'new' ? null : search.edit}
          onClose={() => edit(undefined)}
        />
      )}
    </Page>
  );
}
