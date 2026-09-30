import { Badge, Button, Caption1, makeStyles, Switch, Text, tokens } from '@fluentui/react-components';
import { AddRegular, BedRegular, BuildingRegular, DoorRegular, EditRegular, LayerRegular } from '@fluentui/react-icons';
import { DataTable, EmptyState, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import {
  GENDER_POLICY_LABEL,
  inpatientQueries,
  WARD_TYPE_LABEL,
  type BedDto,
  type RoomDto,
  type WardDto,
} from '@/modules/inpatient/api';
import { nextBedNumber } from '@/modules/inpatient/bedState';
import { BedFeatures, BedStateChip } from '@/modules/inpatient/BedTile';
import { useInpatientPermissions } from '@/modules/inpatient/hooks';
import { BedDialog, BulkBedsDialog, RoomDialog, WardDrawer } from '@/modules/inpatient/SetupForms';

export const Route = createFileRoute('/_app/inpatient/setup')({
  validateSearch: (s: Record<string, unknown>): { wardId?: string } => ({
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
  }),
  component: WardSetupPage,
});

const useStyles = makeStyles({
  columns: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'minmax(0, 5fr) minmax(0, 7fr)',
    alignItems: 'start',
    '@media (max-width: 1200px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  stack: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
});

type Editing =
  | { kind: 'ward'; ward: WardDto | null }
  | { kind: 'room'; room: RoomDto | null }
  | { kind: 'bed'; bed: BedDto | null }
  | { kind: 'bulk' }
  | null;

/** Ward, room and bed masters (WB-01). Beds are added in bulk for new wards; each bed's live status stays on the board. */
function WardSetupPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const permissions = useInpatientPermissions();
  const [showClosed, setShowClosed] = useState(false);
  const [editing, setEditing] = useState<Editing>(null);
  const wards = useQuery(inpatientQueries.wards(true));
  const list = (wards.data ?? []).filter((w) => showClosed || w.isActive);
  const selected = wards.data?.find((w) => w.id === search.wardId) ?? list[0] ?? null;
  const rooms = useQuery({ ...inpatientQueries.rooms(selected?.id ?? ''), enabled: Boolean(selected) });
  const beds = useQuery({ ...inpatientQueries.beds(selected?.id ?? null, true), enabled: Boolean(selected) });

  const select = (ward: WardDto) => void navigate({ search: { wardId: ward.id }, replace: true });
  const roomList = rooms.data ?? [];
  const bedList = beds.data ?? [];
  const nextOrder = bedList.reduce((max, b) => Math.max(max, b.displayOrder), 0) + 10;

  if (!permissions.masters) {
    return (
      <Page maxWidth={720}>
        <EmptyState
          title="You cannot change wards and beds"
          description="Ask the hospital administrator to set up wards, rooms and beds."
        />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Wards and beds"
        icon={<BuildingRegular />}
        subtitle="Set up wards, their rooms and beds. Bed status (vacant, occupied…) is managed on the bed board."
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing({ kind: 'ward', ward: null })}>
            Add ward
          </Button>
        }
      />
      <div className={styles.columns}>
        <Section
          title="Wards"
          actions={<Switch checked={showClosed} onChange={(_, d) => setShowClosed(d.checked)} label="Show closed" />}
          flush
        >
          <DataTable
            ariaLabel="Wards"
            rows={wards.data ? list : undefined}
            loading={wards.isLoading}
            error={wards.error}
            onRetry={() => void wards.refetch()}
            getRowId={(w) => w.id}
            onRowClick={select}
            size="small"
            columns={[
              {
                key: 'name',
                header: 'Ward',
                render: (w) => (
                  <span>
                    <Text weight={w.id === selected?.id ? 'bold' : 'semibold'}>{w.name}</Text>{' '}
                    <Caption1>
                      {w.code} · {WARD_TYPE_LABEL[w.type] ?? w.type}
                    </Caption1>
                    {w.id === selected?.id && (
                      <Badge size="small" appearance="tint" color="brand" style={{ marginLeft: 6 }}>
                        Selected
                      </Badge>
                    )}
                  </span>
                ),
              },
              { key: 'beds', header: 'Beds', width: 90, render: (w) => `${w.occupiedBeds}/${w.totalBeds}` },
              {
                key: 'tariff',
                header: 'Tariff',
                width: 100,
                priority: 'low',
                render: (w) => w.bedServiceCode ?? <Caption1>None</Caption1>,
              },
              {
                key: 'status',
                header: 'Status',
                width: 90,
                render: (w) => (
                  <StatusBadge
                    status={w.isActive ? 'Active' : 'Closed'}
                    tone={w.isActive ? 'success' : 'informative'}
                  />
                ),
              },
            ]}
            rowActions={(w) => (
              <Button
                size="small"
                appearance="subtle"
                icon={<EditRegular />}
                aria-label={`Edit ${w.name}`}
                onClick={() => setEditing({ kind: 'ward', ward: w })}
              />
            )}
            emptyTitle="No wards yet"
            emptyDescription="Add the first ward, then its rooms and beds."
          />
        </Section>

        {selected ? (
          <div className={styles.stack}>
            <Section
              title={selected.name}
              description={[
                WARD_TYPE_LABEL[selected.type],
                GENDER_POLICY_LABEL[selected.genderPolicy],
                selected.floor ? `Floor ${selected.floor}` : null,
                selected.bedServiceCode ? `Tariff ${selected.bedServiceCode}` : 'No bed tariff set',
              ]
                .filter(Boolean)
                .join(' · ')}
              actions={
                <Button icon={<EditRegular />} onClick={() => setEditing({ kind: 'ward', ward: selected })}>
                  Edit ward
                </Button>
              }
            >
              {!selected.bedServiceItemId && (
                <Caption1 style={{ color: tokens.colorPaletteMarigoldForeground1 }}>
                  Set a bed tariff so bed days in this ward are charged automatically.
                </Caption1>
              )}
            </Section>

            <Section
              title={`Rooms (${roomList.length})`}
              description="Optional: open wards have no rooms."
              actions={
                <Button icon={<DoorRegular />} onClick={() => setEditing({ kind: 'room', room: null })}>
                  Add room
                </Button>
              }
              flush
            >
              <DataTable
                ariaLabel="Rooms"
                rows={rooms.data}
                loading={rooms.isLoading}
                error={rooms.error}
                onRetry={() => void rooms.refetch()}
                getRowId={(r) => r.id}
                onRowClick={(r) => setEditing({ kind: 'room', room: r })}
                size="small"
                columns={[
                  {
                    key: 'number',
                    header: 'Room',
                    width: 100,
                    render: (r) => <Text weight="semibold">{r.number}</Text>,
                  },
                  { key: 'desc', header: 'Description', render: (r) => r.description ?? '—' },
                  { key: 'beds', header: 'Beds', width: 70, render: (r) => r.beds },
                  {
                    key: 'flags',
                    header: 'Type',
                    width: 140,
                    render: (r) => (
                      <span style={{ display: 'inline-flex', gap: 4 }}>
                        {r.isIsolation && (
                          <Badge appearance="tint" color="warning">
                            Isolation
                          </Badge>
                        )}
                        {!r.isActive && <Badge appearance="tint">Not in use</Badge>}
                        {r.isActive && !r.isIsolation && <Caption1>Standard</Caption1>}
                      </span>
                    ),
                  },
                ]}
                emptyTitle="No rooms"
                emptyDescription="Add rooms for private and semi-private wards."
              />
            </Section>

            <Section
              title={`Beds (${bedList.filter((b) => b.isActive).length} in service)`}
              actions={
                <>
                  <Button icon={<LayerRegular />} onClick={() => setEditing({ kind: 'bulk' })}>
                    Add many beds
                  </Button>
                  <Button icon={<BedRegular />} onClick={() => setEditing({ kind: 'bed', bed: null })}>
                    Add bed
                  </Button>
                </>
              }
              flush
            >
              <DataTable
                ariaLabel="Beds"
                rows={beds.data}
                loading={beds.isLoading}
                error={beds.error}
                onRetry={() => void beds.refetch()}
                getRowId={(b) => b.id}
                onRowClick={(b) => setEditing({ kind: 'bed', bed: b })}
                size="small"
                columns={[
                  {
                    key: 'label',
                    header: 'Bed',
                    width: 110,
                    render: (b) => (
                      <Text weight="semibold" font="monospace">
                        {b.label}
                      </Text>
                    ),
                  },
                  { key: 'room', header: 'Room', width: 90, render: (b) => b.roomNumber ?? '—' },
                  {
                    key: 'status',
                    header: 'Status',
                    width: 150,
                    render: (b) =>
                      b.isActive ? <BedStateChip status={b.status} /> : <Badge appearance="tint">Out of service</Badge>,
                  },
                  {
                    key: 'equipment',
                    header: 'Equipment',
                    priority: 'low',
                    render: (b) => (
                      <BedFeatures oxygen={b.hasOxygen} ventilator={b.hasVentilator} monitor={b.hasMonitor} />
                    ),
                  },
                  { key: 'order', header: 'Order', width: 70, priority: 'low', render: (b) => b.displayOrder },
                ]}
                emptyTitle="No beds in this ward"
                emptyDescription="Use Add many beds to create the ward's beds in one go."
              />
            </Section>
          </div>
        ) : (
          <EmptyState
            icon={<BuildingRegular />}
            title="Choose a ward"
            description="Select a ward to manage its rooms and beds, or add a new ward."
          />
        )}
      </div>

      {editing?.kind === 'ward' && <WardDrawer ward={editing.ward} onClose={() => setEditing(null)} />}
      {editing?.kind === 'room' && selected && (
        <RoomDialog wardId={selected.id} room={editing.room} onClose={() => setEditing(null)} />
      )}
      {editing?.kind === 'bed' && selected && (
        <BedDialog
          wardId={selected.id}
          bed={editing.bed}
          rooms={roomList.filter((r) => r.isActive || r.id === editing.bed?.roomId)}
          nextOrder={nextOrder}
          onClose={() => setEditing(null)}
        />
      )}
      {editing?.kind === 'bulk' && selected && (
        <BulkBedsDialog
          ward={selected}
          rooms={roomList.filter((r) => r.isActive)}
          startNumber={nextBedNumber(
            selected.code,
            bedList.map((b) => b.label),
          )}
          onClose={() => setEditing(null)}
        />
      )}
    </Page>
  );
}
