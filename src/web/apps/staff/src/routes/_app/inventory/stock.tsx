import { Badge, Button, Caption1, Field, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { HistoryRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { inventoryQueries, type StockSummaryRow } from '@/modules/inventory/api';
import { BatchDrawer } from '@/modules/inventory/BatchDrawer';
import { formatQty } from '@/modules/inventory/format';
import { ITEM_TYPES, itemTypeLabel, type ItemType } from '@/modules/inventory/labels';
import { StorePicker, StoreProblem } from '@/modules/inventory/StorePicker';
import { ExpiryText } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';

interface StockSearch {
  storeId?: string;
  q?: string;
  type?: ItemType;
  below?: boolean;
  page?: number;
}

export const Route = createFileRoute('/_app/inventory/stock')({
  validateSearch: (s: Record<string, unknown>): StockSearch => ({
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    type: ITEM_TYPES.includes(s.type as ItemType) ? (s.type as ItemType) : undefined,
    below: s.below === true || s.below === 'true' ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: StockPage,
});

/** Stock position per store (PH-04): on hand, reserved, usable, expired and value; open an item for its batches. */
function StockPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const working = useWorkingStore('inventory');
  const storeId = search.storeId ?? working.storeId;
  const store = working.stores.find((s) => s.id === storeId);
  const [term, setTerm] = useState(search.q ?? '');
  const [drill, setDrill] = useState<StockSummaryRow | null>(null);
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const stock = useQuery({ ...inventoryQueries.stock(storeId ?? '', { search: search.q, type: search.type, belowReorder: search.below, page: search.page }), enabled: Boolean(storeId) });
  const set = (patch: Partial<StockSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Stock and batches"
        subtitle={store ? `${store.name}: what is on the shelf, set aside and usable. Open an item to see its batches.` : 'Choose a store'}
        actions={
          <Button icon={<HistoryRegular />} onClick={() => void navigate({ to: '/inventory/ledger', search: { storeId: storeId ?? undefined } })}>
            Stock ledger
          </Button>
        }
      />
      <StoreProblem working={working} />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <StorePicker
              stores={working.stores}
              value={storeId}
              loading={working.loading}
              onChange={(id) => {
                if (id) {
                  working.setStoreId(id);
                }
                set({ storeId: id ?? undefined });
              }}
            />
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Item name, code or barcode" />
            </Field>
            <Field label="Type">
              <Select value={search.type ?? ''} onChange={(_, d) => set({ type: (d.value || undefined) as ItemType | undefined })}>
                <option value="">All</option>
                {ITEM_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {itemTypeLabel[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch checked={search.below === true} onChange={(_, d) => set({ below: d.checked || undefined })} label="Only items to reorder" />
          </FilterBar>
        </div>
        {!storeId ? (
          <EmptyState title="Choose a store" description="Stock is kept per store. Pick the store to see its items and batches." />
        ) : (
          <DataTable<StockSummaryRow>
            ariaLabel="Stock"
            rows={stock.data?.items}
            loading={stock.isLoading}
            error={stock.error}
            onRetry={() => void stock.refetch()}
            getRowId={(r) => r.itemId}
            onRowClick={setDrill}
            size="small"
            columns={[
              { key: 'code', header: 'Code', width: 110, priority: 'low', render: (r) => <Text font="monospace">{r.itemCode}</Text> },
              {
                key: 'item',
                header: 'Item',
                render: (r) => (
                  <span>
                    <Text weight="semibold">{r.itemName}</Text>{' '}
                    {r.belowReorder && (
                      <Badge size="small" appearance="tint" color="warning">
                        Reorder
                      </Badge>
                    )}
                    <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                      {itemTypeLabel[r.type]}
                    </Caption1>
                  </span>
                ),
              },
              { key: 'onHand', header: 'On hand', align: 'end', render: (r) => formatQty(r.onHand, r.baseUnit) },
              { key: 'reserved', header: 'Reserved', align: 'end', priority: 'low', render: (r) => (r.reserved ? formatQty(r.reserved) : '—') },
              { key: 'available', header: 'Usable', align: 'end', render: (r) => <Text weight="semibold">{formatQty(r.available)}</Text> },
              {
                key: 'expired',
                header: 'Expired',
                align: 'end',
                render: (r) => (r.expiredQuantity > 0 ? <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{formatQty(r.expiredQuantity)}</Text> : '—'),
              },
              { key: 'expiry', header: 'Nearest expiry', render: (r) => <ExpiryText date={r.nearestExpiry} /> },
              { key: 'reorder', header: 'Reorder at', align: 'end', priority: 'low', render: (r) => (r.reorderLevel > 0 ? formatQty(r.reorderLevel) : '—') },
              { key: 'value', header: 'Value at cost', align: 'end', priority: 'low', render: (r) => formatInr(r.valueAtCost) },
            ]}
            emptyTitle={search.q ? 'No item matches' : 'This store holds no stock'}
            emptyDescription="Stock comes in through goods receipts, transfers and indent issues."
            page={search.page ?? 1}
            pageSize={25}
            totalCount={stock.data?.totalCount}
            onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
          />
        )}
      </Section>
      {drill && storeId && (
        <BatchDrawer storeId={storeId} storeName={store?.name ?? 'this store'} itemId={drill.itemId} itemName={drill.itemName} baseUnit={drill.baseUnit} onClose={() => setDrill(null)} />
      )}
    </Page>
  );
}
