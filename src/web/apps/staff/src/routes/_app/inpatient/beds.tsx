import {
  Button,
  Caption1,
  Field,
  makeStyles,
  ProgressBar,
  SearchBox,
  Select,
  Skeleton,
  SkeletonItem,
  Text,
  tokens,
  Tooltip,
  useArrowNavigationGroup,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  BedRegular,
  BroomRegular,
  PersonAddRegular,
  SettingsRegular,
} from '@fluentui/react-icons';
import { formatTime, useDebouncedValue } from '@hc360/core';
import { EmptyState, ErrorState, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { inpatientQueries, WARD_TYPE_LABEL, type BedBoardItem, type BedBoardWard } from '@/modules/inpatient/api';
import { BedDrawer } from '@/modules/inpatient/BedDrawer';
import { BED_STATUSES, matchesBedSearch, occupancyBand, type BedStatus } from '@/modules/inpatient/bedState';
import { BedLegend, BedTile } from '@/modules/inpatient/BedTile';
import { useInpatientPermissions, useInpatientRealtime, useNow } from '@/modules/inpatient/hooks';

interface BedBoardSearch {
  wardId?: string;
  status?: BedStatus;
  q?: string;
}

export const Route = createFileRoute('/_app/inpatient/beds')({
  validateSearch: (s: Record<string, unknown>): BedBoardSearch => ({
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
    status: BED_STATUSES.includes(s.status as BedStatus) ? (s.status as BedStatus) : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
  }),
  component: BedBoardPage,
});

const useStyles = makeStyles({
  toolbar: { display: 'flex', flexDirection: 'column', gap: '12px' },
  grid: {
    display: 'grid',
    gap: '10px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(168px, 1fr))',
    '@media (max-width: 640px)': { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' },
  },
  wardMeta: { display: 'flex', alignItems: 'center', gap: '10px', minWidth: '220px' },
  bar: { width: '120px' },
  muted: { color: tokens.colorNeutralForeground3 },
  wards: { display: 'flex', flexDirection: 'column', gap: '16px' },
});

/** Live bed board (WB-02): every ward's beds as coloured, labelled tiles; click a bed for its actions. */
function BedBoardPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const permissions = useInpatientPermissions();
  const now = useNow(60_000);
  useInpatientRealtime();

  const wards = useQuery(inpatientQueries.wards());
  const board = useQuery(inpatientQueries.board(search.wardId ?? null));
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 250);
  const [selected, setSelected] = useState<{ bedId: string; wardId: string } | null>(null);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const data = board.data ?? [];
  const totals = data.reduce(
    (sum, ward) => {
      sum.Vacant += ward.counts.vacant;
      sum.Occupied += ward.counts.occupied;
      sum.Reserved += ward.counts.reserved;
      sum.Cleaning += ward.counts.cleaning;
      sum.Blocked += ward.counts.blocked;
      sum.Maintenance += ward.counts.maintenance;
      sum.total += ward.counts.total;
      return sum;
    },
    { Vacant: 0, Occupied: 0, Reserved: 0, Cleaning: 0, Blocked: 0, Maintenance: 0, total: 0 },
  );

  const visible = data
    .map((ward) => ({
      ...ward,
      beds: ward.beds.filter((bed) => (!search.status || bed.status === search.status) && matchesBedSearch(bed, term)),
    }))
    .filter((ward) => ward.beds.length > 0);

  const selectedWard = selected ? data.find((w) => w.wardId === selected.wardId) : undefined;
  const selectedBed = selectedWard?.beds.find((b) => b.bedId === selected?.bedId);

  const setStatus = (status: string) =>
    void navigate({
      search: (prev) => ({ ...prev, status: prev.status === status ? undefined : (status as BedStatus) }),
      replace: true,
    });

  return (
    <Page>
      <PageHeader
        title="Bed board"
        icon={<BedRegular />}
        subtitle={
          board.data
            ? `Live · ${totals.Occupied} occupied, ${totals.Vacant} vacant of ${totals.total} beds · updated ${formatTime(new Date(board.dataUpdatedAt).toISOString())}`
            : 'Live bed status across wards'
        }
        actions={
          <>
            <Tooltip content="Refresh now (the board also updates by itself)" relationship="label">
              <Button
                icon={<ArrowClockwiseRegular />}
                onClick={() => void board.refetch()}
                disabled={board.isFetching}
                aria-label="Refresh"
              />
            </Tooltip>
            {(permissions.housekeeping || permissions.manageBeds) && (
              <Button icon={<BroomRegular />} onClick={() => void navigate({ to: '/inpatient/housekeeping' })}>
                Housekeeping{totals.Cleaning ? ` (${totals.Cleaning})` : ''}
              </Button>
            )}
            {permissions.admit && (
              <Button
                appearance="primary"
                icon={<PersonAddRegular />}
                onClick={() => void navigate({ to: '/inpatient/admit' })}
              >
                Admit patient
              </Button>
            )}
          </>
        }
      />

      <Section>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Ward">
              <Select
                value={search.wardId ?? ''}
                onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, wardId: d.value || undefined }) })}
              >
                <option value="">All wards</option>
                {wards.data?.map((w) => (
                  <option key={w.id} value={w.id}>
                    {w.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Find" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Bed, room, patient name, UHID or IP number"
                aria-label="Find a bed or patient"
              />
            </Field>
          </FilterBar>
          <BedLegend counts={totals} active={search.status ?? null} onToggle={setStatus} />
        </div>
      </Section>

      {board.error && !board.data ? (
        <ErrorState error={board.error} onRetry={() => void board.refetch()} title="Could not load the bed board" />
      ) : board.isLoading ? (
        <Section>
          <div className={styles.grid} aria-busy="true">
            {Array.from({ length: 12 }, (_, i) => (
              <Skeleton key={i} aria-label="Loading beds">
                <SkeletonItem style={{ height: 118 }} />
              </Skeleton>
            ))}
          </div>
        </Section>
      ) : data.length === 0 ? (
        <EmptyState
          icon={<BedRegular />}
          title="No wards set up yet"
          description="Add wards, rooms and beds first. They appear here with their live status."
          action={
            permissions.masters ? (
              <Button icon={<SettingsRegular />} onClick={() => void navigate({ to: '/inpatient/setup' })}>
                Set up wards and beds
              </Button>
            ) : undefined
          }
        />
      ) : visible.length === 0 ? (
        <EmptyState
          title="No bed matches"
          description="Clear the search or the status filter to see all beds."
          action={
            <Button
              onClick={() => {
                setTerm('');
                void navigate({ search: (prev) => ({ ...prev, status: undefined, q: undefined }) });
              }}
            >
              Clear filters
            </Button>
          }
        />
      ) : (
        <div className={styles.wards}>
          {visible.map((ward) => (
            <WardSection
              key={ward.wardId}
              ward={ward}
              now={now}
              selectedBedId={selected?.bedId ?? null}
              onOpen={(bed) => setSelected({ bedId: bed.bedId, wardId: ward.wardId })}
            />
          ))}
        </div>
      )}

      {selectedBed && selectedWard && (
        <BedDrawer bed={selectedBed} ward={selectedWard} onClose={() => setSelected(null)} />
      )}
    </Page>
  );
}

function WardSection({
  ward,
  now,
  selectedBedId,
  onOpen,
}: {
  ward: BedBoardWard;
  now: number;
  selectedBedId: string | null;
  onOpen: (bed: BedBoardItem) => void;
}) {
  const styles = useStyles();
  const navigation = useArrowNavigationGroup({ axis: 'grid-linear', memorizeCurrent: true });
  const percent = ward.counts.occupancyPercent;
  const band = occupancyBand(percent);
  const details = [
    WARD_TYPE_LABEL[ward.type] ?? ward.type,
    ward.floor ? `Floor ${ward.floor}` : null,
    ward.nurseStation ? `Nurse station ${ward.nurseStation}` : null,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <Section
      title={ward.name}
      description={details}
      actions={
        <div className={styles.wardMeta}>
          <Text size={200}>
            <strong>{ward.counts.vacant}</strong> vacant · {ward.counts.occupied}/{ward.counts.operational} occupied (
            {percent}%)
          </Text>
          <ProgressBar
            className={styles.bar}
            value={Math.min(1, percent / 100)}
            thickness="large"
            color={band === 'full' ? 'error' : band === 'busy' ? 'warning' : 'brand'}
            aria-label={`${ward.name} ${percent}% occupied`}
          />
        </div>
      }
    >
      <div className={styles.grid} {...navigation}>
        {ward.beds.map((bed) => (
          <BedTile key={bed.bedId} bed={bed} now={now} selected={bed.bedId === selectedBedId} onOpen={onOpen} />
        ))}
      </div>
      {ward.beds.length < ward.counts.total && (
        <Caption1 className={styles.muted} style={{ display: 'block', marginTop: 8 }}>
          Showing {ward.beds.length} of {ward.counts.total} beds (filtered).
        </Caption1>
      )}
    </Section>
  );
}
