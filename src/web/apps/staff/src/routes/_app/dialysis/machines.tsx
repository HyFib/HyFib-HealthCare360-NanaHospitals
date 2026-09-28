import { Button, Caption1, Switch, Text, tokens } from '@fluentui/react-components';
import { AddRegular, BroomRegular } from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { DataTable, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { dialysisKeys, dialysisQueries, SERO_STATUSES, type DialysisMachineDto } from '@/modules/dialysis/api';
import { DisinfectionState, MachineStatusBadge, SeroBadge } from '@/modules/dialysis/DialysisBadges';
import { DisinfectionDialog } from '@/modules/dialysis/DialysisDialogs';
import { MachineDrawer } from '@/modules/dialysis/MachineDrawer';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';

export const Route = createFileRoute('/_app/dialysis/machines')({
  component: DialysisMachinesPage,
});

/** Machine and station master with sero dedication and disinfection after every session. */
function DialysisMachinesPage() {
  const navigate = useNavigate();
  const facilityId = useFacilityId();
  const canRead = usePermission('dialysis.read');
  const canManageMachines = usePermission('dialysis.machines.manage');
  const canDisinfect = usePermission('dialysis.sessions.manage');
  const [showInactive, setShowInactive] = useState(false);
  const [editing, setEditing] = useState<DialysisMachineDto | 'new' | null>(null);
  const [disinfecting, setDisinfecting] = useState<DialysisMachineDto | null>(null);
  const machines = useQuery({ ...dialysisQueries.machines(showInactive), enabled: canRead });

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'dialysis.board.changed', [['dialysis', 'machines'], dialysisKeys.boards]);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="dialysis machines" />
      </Page>
    );
  }

  const rows = [...(machines.data ?? [])].sort(
    (a, b) => SERO_STATUSES.indexOf(a.seroDedication) - SERO_STATUSES.indexOf(b.seroDedication) || a.code.localeCompare(b.code),
  );

  return (
    <Page>
      <PageHeader
        title="Dialysis machines"
        subtitle="Each machine is dedicated to one sero category and must be disinfected after every session."
        breadcrumbs={[{ label: 'Dialysis board', onClick: () => void navigate({ to: '/dialysis' }) }, { label: 'Machines' }]}
        actions={
          canManageMachines && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add machine
            </Button>
          )
        }
      />
      <Section flush actions={<Switch checked={showInactive} onChange={(_, d) => setShowInactive(d.checked)} label="Show inactive" />}>
        <DataTable
          ariaLabel="Dialysis machines"
          rows={machines.data ? rows : undefined}
          loading={machines.isLoading}
          error={machines.error}
          onRetry={() => void machines.refetch()}
          getRowId={(m) => m.id}
          onRowClick={canManageMachines ? (m) => setEditing(m) : undefined}
          columns={[
            {
              key: 'code',
              header: 'Machine',
              render: (m) => (
                <span>
                  <Text weight="semibold">{m.code}</Text>
                  {m.station && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {m.station}
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'sero', header: 'Dedicated to', render: (m) => <SeroBadge sero={m.seroDedication} long /> },
            { key: 'status', header: 'Status', render: (m) => (m.isActive ? <MachineStatusBadge status={m.status} /> : <Caption1>Inactive</Caption1>) },
            {
              key: 'disinfection',
              header: 'Disinfection',
              render: (m) => (
                <span>
                  <DisinfectionState needsDisinfection={m.needsDisinfection} lastDisinfectedAt={m.lastDisinfectedAt} />
                  {m.lastDisinfectionMethod && (
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {m.lastDisinfectionMethod}
                    </Caption1>
                  )}
                </span>
              ),
            },
            { key: 'used', header: 'Last used', priority: 'low', render: (m) => (m.lastUsedAt ? formatRelative(m.lastUsedAt) : 'Not used yet') },
            { key: 'model', header: 'Model', priority: 'low', render: (m) => [m.manufacturer, m.model].filter(Boolean).join(' ') },
          ]}
          rowActions={
            canDisinfect
              ? (m) =>
                  m.status !== 'InUse' && m.isActive ? (
                    <Button size="small" appearance={m.needsDisinfection ? 'primary' : 'subtle'} icon={<BroomRegular />} onClick={() => setDisinfecting(m)}>
                      Record disinfection
                    </Button>
                  ) : null
              : undefined
          }
          emptyTitle="No dialysis machines"
          emptyDescription={canManageMachines ? 'Add each machine with the sero category it is dedicated to.' : undefined}
        />
      </Section>
      {editing && <MachineDrawer machine={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {disinfecting && <DisinfectionDialog machine={disinfecting} onClose={() => setDisinfecting(null)} />}
    </Page>
  );
}
