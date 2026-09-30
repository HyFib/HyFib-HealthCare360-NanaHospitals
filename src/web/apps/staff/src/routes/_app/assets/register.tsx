import { Badge, Button, Caption1, Field, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatDateOnly, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState, type KeyboardEvent } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { assetApi, assetQueries, type AssetFilters, type AssetListItem } from '@/modules/assets/api';
import { AssetDrawer } from '@/modules/assets/AssetDrawer';
import {
  ASSET_CATEGORIES,
  ASSET_STATUSES,
  assetCategoryLabel,
  assetStatusLabel,
  assetStatusTone,
  CRITICALITIES,
  criticalityTone,
  type AssetCategory,
  type AssetCriticality,
  type AssetStatus,
} from '@/modules/assets/labels';
import { daysBetween } from '@/modules/inventory/format';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';

export const Route = createFileRoute('/_app/assets/register')({
  validateSearch: (s: Record<string, unknown>): AssetFilters => ({
    search: typeof s.search === 'string' && s.search ? s.search : undefined,
    category: ASSET_CATEGORIES.includes(s.category as AssetCategory) ? (s.category as AssetCategory) : undefined,
    status: ASSET_STATUSES.includes(s.status as AssetStatus) ? (s.status as AssetStatus) : undefined,
    criticality: CRITICALITIES.includes(s.criticality as AssetCriticality)
      ? (s.criticality as AssetCriticality)
      : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    includeRetired: s.includeRetired === true || s.includeRetired === 'true' ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: AssetRegisterPage,
});

function dueCell(date: string | null, today: string) {
  if (!date) {
    return '—';
  }
  const days = daysBetween(today, date);
  return (
    <span
      style={
        days < 0
          ? { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold }
          : days <= 7
            ? { color: tokens.colorPaletteMarigoldForeground1 }
            : undefined
      }
    >
      {formatDateOnly(date)}
    </span>
  );
}

/** Fixed asset register (AST-01). Scan an asset tag into the search box to open it straight away. */
function AssetRegisterPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const canManage = usePermission(S.assetsManage);
  const departments = useDepartments();
  const [term, setTerm] = useState(search.search ?? '');
  const [adding, setAdding] = useState(false);
  const debounced = useDebouncedValue(term.trim(), 300);
  const today = todayIso();

  useEffect(() => {
    if ((debounced || undefined) !== search.search) {
      void navigate({
        search: (prev) => ({ ...prev, search: debounced || undefined, page: undefined }),
        replace: true,
      });
    }
  }, [debounced, search.search, navigate]);

  const assets = useQuery(assetQueries.list(search));
  const set = (patch: Partial<AssetFilters>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const open = (id: string) => void navigate({ to: '/assets/$assetId', params: { assetId: id } });

  const onScan = async (event: KeyboardEvent<HTMLInputElement>) => {
    const tag = term.trim();
    if (event.key !== 'Enter' || tag.length < 3) {
      return;
    }
    try {
      const asset = await assetApi.byTag(tag);
      open(asset.id);
    } catch {
      notify.info('No asset with that tag', 'Showing the closest matches instead.');
    }
  };

  return (
    <Page>
      <PageHeader
        title="Asset register"
        subtitle="Every piece of equipment with its tag, location, warranty and upkeep. Scan a tag and press Enter to open it."
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setAdding(true)}>
              Add asset
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                onKeyDown={(e) => void onScan(e)}
                placeholder="Tag, name, model or serial number"
                autoFocus
              />
            </Field>
            <Field label="Category">
              <Select
                value={search.category ?? ''}
                onChange={(_, d) => set({ category: (d.value || undefined) as AssetCategory | undefined })}
              >
                <option value="">All</option>
                {ASSET_CATEGORIES.map((c) => (
                  <option key={c} value={c}>
                    {assetCategoryLabel[c]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as AssetStatus | undefined })}
              >
                <option value="">All in service</option>
                {ASSET_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {assetStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Criticality">
              <Select
                value={search.criticality ?? ''}
                onChange={(_, d) => set({ criticality: (d.value || undefined) as AssetCriticality | undefined })}
              >
                <option value="">All</option>
                {CRITICALITIES.map((c) => (
                  <option key={c} value={c}>
                    {c}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Department">
              <Select
                value={search.departmentId ?? ''}
                onChange={(_, d) => set({ departmentId: d.value || undefined })}
              >
                <option value="">All</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={search.includeRetired === true}
              onChange={(_, d) => set({ includeRetired: d.checked || undefined })}
              label="Include condemned and disposed"
              disabled={Boolean(search.status)}
            />
          </FilterBar>
        </div>
        <DataTable<AssetListItem>
          ariaLabel="Assets"
          rows={assets.data?.items}
          loading={assets.isLoading}
          error={assets.error}
          onRetry={() => void assets.refetch()}
          getRowId={(a) => a.id}
          onRowClick={(a) => open(a.id)}
          size="small"
          columns={[
            { key: 'tag', header: 'Tag', width: 120, render: (a) => <Text font="monospace">{a.assetTag}</Text> },
            {
              key: 'name',
              header: 'Equipment',
              render: (a) => (
                <span>
                  <Text weight="semibold">{a.name}</Text>{' '}
                  {a.openTickets > 0 && (
                    <Badge size="small" color="danger">
                      {a.openTickets} open
                    </Badge>
                  )}
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {[a.make, a.model, a.serialNumber ? `S/N ${a.serialNumber}` : null].filter(Boolean).join(' · ')}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'where',
              header: 'Where',
              priority: 'low',
              render: (a) => [a.departmentName, a.location].filter(Boolean).join(' · ') || '—',
            },
            {
              key: 'crit',
              header: 'Criticality',
              width: 100,
              render: (a) => (
                <Badge appearance="tint" color={criticalityTone[a.criticality]}>
                  {a.criticality}
                </Badge>
              ),
            },
            { key: 'pm', header: 'Next PM', priority: 'low', render: (a) => dueCell(a.nextPmDue, today) },
            { key: 'cal', header: 'Calibration', priority: 'low', render: (a) => dueCell(a.nextCalibrationDue, today) },
            { key: 'warranty', header: 'Warranty', priority: 'low', render: (a) => dueCell(a.warrantyExpiry, today) },
            {
              key: 'status',
              header: 'Status',
              render: (a) => (
                <StatusBadge status={a.status} tone={assetStatusTone[a.status]} label={assetStatusLabel[a.status]} />
              ),
            },
          ]}
          emptyTitle={search.search ? 'No asset matches' : 'No assets registered'}
          emptyDescription={canManage ? 'Add equipment with its make, model, serial number and warranty.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={assets.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {adding && <AssetDrawer asset={null} onClose={() => setAdding(false)} onCreated={open} />}
    </Page>
  );
}
