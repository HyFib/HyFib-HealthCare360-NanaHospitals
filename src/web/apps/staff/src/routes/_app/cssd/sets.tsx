import { Button, Caption1, Switch, Text } from '@fluentui/react-components';
import { AddRegular, BoxRegular, EditRegular } from '@fluentui/react-icons';
import { DataTable, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { cssdQueries, type InstrumentSetDto } from '@/modules/cssd/api';
import { METHOD_LABEL } from '@/modules/cssd/rules';
import { SetDrawer } from '@/modules/cssd/SetDrawer';

export const Route = createFileRoute('/_app/cssd/sets')({
  component: InstrumentSetsPage,
});

/** Instrument sets and their count sheets (CSSD-01). */
function InstrumentSetsPage() {
  const canManage = usePermission('cssd.masters.manage');
  const [showInactive, setShowInactive] = useState(false);
  const [editing, setEditing] = useState<InstrumentSetDto | 'new' | null>(null);
  const sets = useQuery(cssdQueries.sets(showInactive));

  return (
    <Page>
      <PageHeader
        title="Instrument sets"
        icon={<BoxRegular />}
        subtitle="Trays and packs with their count sheets, method and sterile shelf life"
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add set
            </Button>
          )
        }
      />
      <Section
        flush
        actions={
          <Switch checked={showInactive} onChange={(_, d) => setShowInactive(d.checked)} label="Show sets not in use" />
        }
      >
        <DataTable
          ariaLabel="Instrument sets"
          rows={sets.data}
          loading={sets.isLoading}
          error={sets.error}
          onRetry={() => void sets.refetch()}
          getRowId={(s) => s.id}
          onRowClick={canManage ? (s) => setEditing(s) : undefined}
          columns={[
            {
              key: 'name',
              header: 'Set',
              render: (s) => (
                <span>
                  <Text weight="semibold">{s.name}</Text> <Caption1>{s.code}</Caption1>
                  {s.category && <Caption1 block>{s.category}</Caption1>}
                </span>
              ),
            },
            { key: 'method', header: 'Method', render: (s) => METHOD_LABEL[s.method] },
            { key: 'count', header: 'Instruments', width: 110, align: 'end', render: (s) => s.instrumentCount },
            {
              key: 'shelf',
              header: 'Sterile for',
              width: 110,
              align: 'end',
              priority: 'low',
              render: (s) => `${s.shelfLifeDays} days`,
            },
            { key: 'stock', header: 'On shelf', width: 100, align: 'end', render: (s) => s.packsAvailable },
            {
              key: 'status',
              header: 'Status',
              width: 100,
              render: (s) => (
                <StatusBadge
                  status={s.isActive ? 'Active' : 'Inactive'}
                  label={s.isActive ? 'In use' : 'Not in use'}
                  tone={s.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          rowActions={
            canManage
              ? (s) => (
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<EditRegular />}
                    aria-label={`Edit ${s.name}`}
                    onClick={() => setEditing(s)}
                  />
                )
              : undefined
          }
          emptyTitle="No instrument sets"
          emptyDescription={canManage ? 'Add each tray with its count sheet so returns can be checked.' : undefined}
        />
      </Section>
      {editing && <SetDrawer set={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}
