import {
  Badge,
  Caption1,
  Field,
  makeStyles,
  SearchBox,
  Select,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { LockClosedRegular, NumberSymbolRegular } from '@fluentui/react-icons';
import { useDebouncedValue, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { NoAccess } from '@/modules/admin/NoAccess';
import {
  filterSeries,
  NUMBERING_PERMISSION,
  numberingQueries,
  seriesAreas,
  sortSeries,
  type NumberSeriesItem,
  type SeriesSort,
} from '@/modules/numbering/api';
import { NumberSeriesDrawer } from '@/modules/numbering/NumberSeriesEditor';
import { lastIssuedText, RESTART_LABELS } from '@/modules/numbering/numberFormat';

interface NumberingSearch {
  q?: string;
  area?: string;
  sort?: SeriesSort;
  desc?: boolean;
}

export const Route = createFileRoute('/_app/setup/numbering')({
  validateSearch: (s: Record<string, unknown>): NumberingSearch => ({
    q: typeof s.q === 'string' && s.q.trim() ? s.q.trim().slice(0, 100) : undefined,
    area: typeof s.area === 'string' && s.area ? s.area.slice(0, 64) : undefined,
    sort: s.sort === 'name' ? 'name' : undefined,
    desc: s.desc === true ? true : undefined,
  }),
  component: NumberFormatsPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  // Names and their badges wrap rather than being cut off on narrow screens.
  name: { display: 'flex', flexDirection: 'column', minWidth: 0, whiteSpace: 'normal' },
  badges: { display: 'inline-flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
});

/**
 * Number formats (Hospital setup): how UHIDs, bills, receipts, accession numbers and the other numbers HC360 issues
 * look, when they start again from 1, and the next number of each. Every series is listed before it issues its first
 * number, so the formats can be set before go-live.
 */
function NumberFormatsPage() {
  const styles = useStyles();
  const allowed = usePermission(NUMBERING_PERMISSION);
  const facilityName = useSession((s) => s.user?.facilityName ?? null);
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [editing, setEditing] = useState<string | null>(null);
  const list = useQuery({ ...numberingQueries.list(), enabled: allowed });

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Number formats" />
        <NoAccess what="number formats" />
      </Page>
    );
  }

  const sort = search.sort ?? 'area';
  const rows = list.data
    ? sortSeries(filterSeries(list.data.series, search.area, search.q), sort, search.desc ?? false)
    : undefined;
  const editingSeries = editing ? list.data?.series.find((s) => s.key === editing) : undefined;

  return (
    <Page>
      <PageHeader
        title="Number formats"
        icon={<NumberSymbolRegular />}
        subtitle="How UHIDs, bills, receipts and the other numbers HC360 issues look. Set them up before the first numbers are issued: a change applies to new numbers only."
      />
      <Section
        flush
        description={
          (list.data?.facilityCount ?? 0) > 1
            ? `Formats for ${facilityName ?? 'this facility'}. Shared numbers, such as the UHID, are the same in every facility.`
            : undefined
        }
      >
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox
                value={term}
                onChange={(_, data) => setTerm(data.value)}
                placeholder="Number, area or format"
              />
            </Field>
            <Field label="Area">
              <Select
                value={search.area ?? ''}
                onChange={(_, data) =>
                  void navigate({ search: (prev) => ({ ...prev, area: data.value || undefined }) })
                }
              >
                <option value="">All areas</option>
                {seriesAreas(list.data?.series ?? []).map((area) => (
                  <option key={area} value={area}>
                    {area}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Number formats"
          rows={rows}
          loading={list.isLoading}
          error={list.error}
          onRetry={() => void list.refetch()}
          getRowId={(s) => s.key}
          onRowClick={(s) => setEditing(s.key)}
          sort={{ key: sort, desc: search.desc ?? false }}
          onSortChange={(next) =>
            void navigate({
              search: (prev) => ({
                ...prev,
                sort: next.key === 'name' ? 'name' : undefined,
                desc: next.desc ? true : undefined,
              }),
            })
          }
          columns={[
            { key: 'area', header: 'Area', width: 150, sortable: true, render: (s) => s.area },
            {
              key: 'name',
              header: 'Number',
              sortable: true,
              render: (s) => <SeriesName series={s} />,
            },
            {
              key: 'format',
              header: 'Format',
              width: 190,
              render: (s) => <Text className={styles.mono}>{s.format}</Text>,
            },
            {
              key: 'next',
              header: 'Next number',
              width: 170,
              render: (s) => <Text className={styles.mono}>{s.nextExample}</Text>,
            },
            {
              key: 'restarts',
              header: 'Restarts',
              width: 110,
              priority: 'low',
              render: (s) => RESTART_LABELS[s.resetPolicy],
            },
            {
              key: 'last',
              header: 'Last issued',
              width: 130,
              priority: 'low',
              render: (s) => lastIssuedText(s.lastNumber, s.resetPolicy),
            },
          ]}
          emptyTitle="No number formats match"
          emptyDescription="Clear the search or choose another area."
        />
      </Section>
      {editingSeries && list.data && (
        <NumberSeriesDrawer series={editingSeries} list={list.data} onClose={() => setEditing(null)} />
      )}
    </Page>
  );
}

/** The series' name, whether its numbers are shared by all facilities, and whether it is fixed or changed. */
function SeriesName({ series }: { series: NumberSeriesItem }) {
  const styles = useStyles();
  return (
    <span className={styles.name}>
      <span className={styles.badges}>
        <Text weight="semibold">{series.name}</Text>
        {series.isLocked && (
          <Tooltip content={series.lockedReason ?? 'The format is fixed.'} relationship="description">
            <Badge appearance="tint" color="informative" size="small" icon={<LockClosedRegular />}>
              Fixed format
            </Badge>
          </Tooltip>
        )}
        {series.isCustomised && (
          <Badge appearance="outline" color="brand" size="small">
            Customised
          </Badge>
        )}
      </span>
      {series.scope === 'global' && <Caption1 className={styles.muted}>Shared by all facilities</Caption1>}
    </span>
  );
}
