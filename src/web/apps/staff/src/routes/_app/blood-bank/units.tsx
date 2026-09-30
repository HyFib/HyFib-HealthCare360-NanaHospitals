import { Button, Field, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import { DeleteRegular, PrintRegular } from '@fluentui/react-icons';
import { useDebouncedValue, type BloodGroup } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  bloodKeys,
  bloodQueries,
  COMPONENT_TYPES,
  KNOWN_GROUPS,
  UNIT_STATUSES,
  type BloodComponentType,
  type BloodUnitDto,
  type BloodUnitStatus,
  type UnitFilters,
} from '@/modules/blood-bank/api';
import { ExpiryText, TtiBadge, UnitStatusBadge } from '@/modules/blood-bank/BloodBadges';
import { componentLabel, groupLabel, unitStatusLabel } from '@/modules/blood-bank/labels';
import { printUnitLabels } from '@/modules/blood-bank/print';
import { canDiscardUnit } from '@/modules/blood-bank/rules';
import { DiscardUnitDialog } from '@/modules/blood-bank/UnitActions';
import { NoAccess } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation, useNow } from '@/modules/ot/shared/hooks';

export const Route = createFileRoute('/_app/blood-bank/units')({
  validateSearch: (s: Record<string, unknown>): UnitFilters => ({
    status: UNIT_STATUSES.includes(s.status as BloodUnitStatus) ? (s.status as BloodUnitStatus) : undefined,
    component: COMPONENT_TYPES.includes(s.component as BloodComponentType)
      ? (s.component as BloodComponentType)
      : undefined,
    bloodGroup: (KNOWN_GROUPS as readonly string[]).includes(s.bloodGroup as string)
      ? (s.bloodGroup as BloodGroup)
      : undefined,
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: BloodUnitsPage,
});

/** Every unit with its status, screening and expiry (first expiry first); labels and discards. */
function BloodUnitsPage() {
  const filters = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const facilityId = useFacilityId();
  const now = useNow(60_000);
  const canRead = usePermission('bloodbank.read');
  const canManage = usePermission('bloodbank.inventory.manage');
  const [term, setTerm] = useState(filters.search ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [discarding, setDiscarding] = useState<BloodUnitDto | null>(null);
  const units = useQuery({ ...bloodQueries.units(filters), enabled: canRead });

  useEffect(() => {
    if ((debounced || undefined) !== filters.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, filters.search, navigate]);

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'bloodbank.changed', [bloodKeys.unitLists]);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="blood units" />
      </Page>
    );
  }

  const set = (patch: Partial<UnitFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Blood units"
        subtitle="Sorted by expiry, soonest first. Scan or type a unit number to find a bag."
        breadcrumbs={[
          { label: 'Blood stock', onClick: () => void navigate({ to: '/blood-bank' }) },
          { label: 'Units' },
        ]}
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Unit number" style={{ flex: '1 1 220px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Scan or type (start of the number)"
              />
            </Field>
            <Field label="Status">
              <Select
                value={filters.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as BloodUnitStatus | undefined })}
              >
                <option value="">All</option>
                {UNIT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {unitStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Component">
              <Select
                value={filters.component ?? ''}
                onChange={(_, d) => set({ component: (d.value || undefined) as BloodComponentType | undefined })}
              >
                <option value="">All</option>
                {COMPONENT_TYPES.map((c) => (
                  <option key={c} value={c}>
                    {componentLabel[c]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Group">
              <Select
                value={filters.bloodGroup ?? ''}
                onChange={(_, d) => set({ bloodGroup: (d.value || undefined) as BloodGroup | undefined })}
              >
                <option value="">All</option>
                {KNOWN_GROUPS.map((g) => (
                  <option key={g} value={g}>
                    {groupLabel(g)}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Blood units"
          rows={units.data?.items}
          loading={units.isLoading}
          error={units.error}
          onRetry={() => void units.refetch()}
          getRowId={(u) => u.id}
          columns={[
            {
              key: 'number',
              header: 'Unit',
              render: (u) => <Text style={{ fontFamily: tokens.fontFamilyMonospace }}>{u.unitNumber}</Text>,
            },
            {
              key: 'component',
              header: 'Component',
              render: (u) => `${componentLabel[u.componentType]} · ${u.volumeMl} ml`,
            },
            { key: 'group', header: 'Group', width: 110, render: (u) => groupLabel(u.bloodGroup) },
            { key: 'expiry', header: 'Expiry', render: (u) => <ExpiryText expiresAt={u.expiresAt} now={now} /> },
            { key: 'status', header: 'Status', render: (u) => <UnitStatusBadge status={u.status} /> },
            { key: 'tti', header: 'Screening', priority: 'low', render: (u) => <TtiBadge tti={u.tti} /> },
            { key: 'store', header: 'Stored at', priority: 'low', render: (u) => u.storageLocation ?? '—' },
          ]}
          rowActions={(u) => (
            <span style={{ display: 'inline-flex', gap: 4 }}>
              <Button
                size="small"
                appearance="subtle"
                icon={<PrintRegular />}
                aria-label={`Print label for ${u.unitNumber}`}
                onClick={() =>
                  void printUnitLabels([u]).catch((error: unknown) => notify.error(error, 'Printing failed'))
                }
              />
              {canManage && canDiscardUnit(u.status) && (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label={`Discard ${u.unitNumber}`}
                  onClick={() => setDiscarding(u)}
                />
              )}
            </span>
          )}
          emptyTitle="No units match"
          emptyDescription="Clear the filters, or check the unit number on the label."
          page={filters.page ?? 1}
          pageSize={25}
          totalCount={units.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      <DiscardUnitDialog unit={discarding} onClose={() => setDiscarding(null)} />
    </Page>
  );
}
