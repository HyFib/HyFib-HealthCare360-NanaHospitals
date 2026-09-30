import {
  Button,
  Caption1,
  Field,
  Input,
  SearchBox,
  Select,
  Switch,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, todayIso, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { inventoryQueries, type LedgerRow } from '@/modules/inventory/api';
import { addDays, formatQty } from '@/modules/inventory/format';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import {
  MOVEMENT_TYPES,
  movementLabel,
  referenceModuleLabel,
  type StockMovementType,
} from '@/modules/inventory/labels';
import { CATALOG_READERS } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { ExpiryText } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface LedgerSearch {
  storeId?: string;
  itemId?: string;
  itemName?: string;
  from?: string;
  to?: string;
  movement?: StockMovementType;
  q?: string;
  newest?: boolean;
  page?: number;
}

export const Route = createFileRoute('/_app/inventory/ledger')({
  validateSearch: (s: Record<string, unknown>): LedgerSearch => ({
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    itemId: typeof s.itemId === 'string' && s.itemId ? s.itemId : undefined,
    itemName: typeof s.itemName === 'string' && s.itemName ? s.itemName : undefined,
    from: typeof s.from === 'string' && DATE.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DATE.test(s.to) ? s.to : undefined,
    movement: MOVEMENT_TYPES.includes(s.movement as StockMovementType) ? (s.movement as StockMovementType) : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    newest: s.newest === true || s.newest === 'true' ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: LedgerPage,
});

/** Stock card: every movement with the running balance, who made it and the document behind it. */
function LedgerPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const working = useWorkingStore('inventory');
  const canPickItem = usePermission(CATALOG_READERS);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const today = todayIso();
  const from = search.from ?? addDays(today, -30);
  const to = search.to ?? today;
  const storeNames = new Map(working.stores.map((s) => [s.id, s.name]));

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const ledger = useQuery(
    inventoryQueries.ledger({
      storeId: search.storeId,
      itemId: search.itemId,
      from,
      to,
      movement: search.movement,
      search: search.q,
      page: search.page,
      desc: search.newest,
    }),
  );
  const set = (patch: Partial<LedgerSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Stock ledger"
        subtitle={
          search.itemName
            ? `Stock card of ${search.itemName}`
            : 'Every stock movement with the balance after it. Pick an item to read it as a stock card.'
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <StorePicker
              stores={working.stores}
              value={search.storeId ?? null}
              onChange={(id) => set({ storeId: id ?? undefined })}
              allowAll
            />
            {search.itemId ? (
              <Field label="Item">
                <Button
                  icon={<DismissRegular />}
                  iconPosition="after"
                  onClick={() => set({ itemId: undefined, itemName: undefined })}
                >
                  {search.itemName ?? 'Selected item'}
                </Button>
              </Field>
            ) : (
              canPickItem && (
                <div style={{ minWidth: 260 }}>
                  <ItemPicker
                    label="Item"
                    placeholder="All items, or search one"
                    showStock={false}
                    onPick={(item) => set({ itemId: item.id, itemName: item.name })}
                  />
                </div>
              )
            )}
            <Field label="From">
              <Input type="date" value={from} max={to} onChange={(_, d) => set({ from: d.value || undefined })} />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={to}
                min={from}
                max={today}
                onChange={(_, d) => set({ to: d.value || undefined })}
              />
            </Field>
            <Field label="Movement">
              <Select
                value={search.movement ?? ''}
                onChange={(_, d) => set({ movement: (d.value || undefined) as StockMovementType | undefined })}
              >
                <option value="">All</option>
                {MOVEMENT_TYPES.map((m) => (
                  <option key={m} value={m}>
                    {movementLabel[m]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Document or batch" style={{ flex: '1 1 200px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Exact number" />
            </Field>
            <Switch
              checked={search.newest === true}
              onChange={(_, d) => set({ newest: d.checked || undefined })}
              label="Newest first"
            />
          </FilterBar>
        </div>
        <DataTable<LedgerRow>
          ariaLabel="Stock ledger"
          rows={ledger.data?.items}
          loading={ledger.isLoading}
          error={ledger.error}
          onRetry={() => void ledger.refetch()}
          getRowId={(r) => r.id}
          size="small"
          columns={[
            { key: 'when', header: 'Date and time', width: 160, render: (r) => formatDateTime(r.occurredAt) },
            {
              key: 'item',
              header: 'Item and batch',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.itemName}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {r.batchNumber} · <ExpiryText date={r.expiryDate} />
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'movement',
              header: 'Movement',
              render: (r) => (
                <span>
                  {movementLabel[r.movementType] ?? r.movementType}
                  {r.counterpartStoreId && (
                    <Caption1 block>
                      {r.quantityIn > 0 ? 'from' : 'to'} {storeNames.get(r.counterpartStoreId) ?? 'another store'}
                    </Caption1>
                  )}
                </span>
              ),
            },
            {
              key: 'in',
              header: 'In',
              align: 'end',
              render: (r) =>
                r.quantityIn ? (
                  <Text style={{ color: tokens.colorPaletteGreenForeground1 }}>+{formatQty(r.quantityIn)}</Text>
                ) : (
                  ''
                ),
            },
            {
              key: 'out',
              header: 'Out',
              align: 'end',
              render: (r) =>
                r.quantityOut ? (
                  <Text style={{ color: tokens.colorPaletteRedForeground1 }}>−{formatQty(r.quantityOut)}</Text>
                ) : (
                  ''
                ),
            },
            {
              key: 'balance',
              header: 'Balance',
              align: 'end',
              render: (r) => (
                <Tooltip content={`Batch balance after: ${formatQty(r.batchBalanceAfter)}`} relationship="description">
                  <Text weight="semibold">{formatQty(r.balanceAfter)}</Text>
                </Tooltip>
              ),
            },
            { key: 'cost', header: 'Unit cost', align: 'end', priority: 'low', render: (r) => formatInr(r.unitCost) },
            {
              key: 'doc',
              header: 'Document',
              render: (r) => (
                <span>
                  {referenceModuleLabel(r.referenceModule)}
                  {r.referenceNumber && <Caption1 block>{r.referenceNumber}</Caption1>}
                </span>
              ),
            },
            {
              key: 'by',
              header: 'By',
              priority: 'low',
              render: (r) => (
                <span>
                  {r.userName ?? '—'}
                  {r.remarks && <Caption1 block>{r.remarks}</Caption1>}
                </span>
              ),
            },
          ]}
          emptyTitle="No movements in this period"
          emptyDescription="Widen the dates or clear the filters."
          page={search.page ?? 1}
          pageSize={50}
          totalCount={ledger.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
    </Page>
  );
}
