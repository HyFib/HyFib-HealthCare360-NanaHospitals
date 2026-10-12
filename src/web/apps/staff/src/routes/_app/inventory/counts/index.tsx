import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  ProgressBar,
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { ClipboardTaskListLtrRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import {
  DataTable,
  FilterBar,
  Page,
  PageHeader,
  Section,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { inventoryApi, inventoryKeys, inventoryQueries, type CountListItem } from '@/modules/inventory/api';
import {
  COUNT_STATUSES,
  ITEM_TYPES,
  itemTypeLabel,
  type ItemType,
  type StockCountStatus,
} from '@/modules/inventory/labels';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { DocStatus } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';

interface CountSearch {
  storeId?: string;
  status?: StockCountStatus;
  page?: number;
  start?: boolean;
}

export const Route = createFileRoute('/_app/inventory/counts/')({
  validateSearch: (s: Record<string, unknown>): CountSearch => ({
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    status: COUNT_STATUSES.includes(s.status as StockCountStatus) ? (s.status as StockCountStatus) : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    start: s.start === true || s.start === 'true' ? true : undefined,
  }),
  component: CountsPage,
});

const STATUS_LABEL: Record<StockCountStatus, string> = {
  InProgress: 'Counting',
  Submitted: 'Submitted',
  Adjusted: 'Adjusted',
  Cancelled: 'Cancelled',
};

/** Physical stock counts (INV-07): print or open the count sheet, enter what is on the shelf, submit the differences. */
function CountsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canCount = usePermission(S.counts);
  const working = useWorkingStore('inventory');
  const counts = useQuery(
    inventoryQueries.counts({ storeId: search.storeId, status: search.status, page: search.page }),
  );
  const set = (patch: Partial<CountSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Stock counts"
        subtitle="Count while the store is closed, or one rack or item type at a time, so the book quantity matches what was counted."
        actions={
          canCount && (
            <Button appearance="primary" icon={<ClipboardTaskListLtrRegular />} onClick={() => set({ start: true })}>
              Start a count
            </Button>
          )
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
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as StockCountStatus | undefined })}
              >
                <option value="">All</option>
                {COUNT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable<CountListItem>
          ariaLabel="Stock counts"
          rows={counts.data?.items}
          loading={counts.isLoading}
          error={counts.error}
          onRetry={() => void counts.refetch()}
          getRowId={(c) => c.id}
          onRowClick={(c) => void navigate({ to: '/inventory/counts/$countId', params: { countId: c.id } })}
          size="small"
          columns={[
            { key: 'no', header: 'Count', render: (c) => <Text font="monospace">{c.number}</Text> },
            { key: 'store', header: 'Store', render: (c) => c.storeName },
            { key: 'started', header: 'Started', render: (c) => formatDateTime(c.startedAt) },
            {
              key: 'progress',
              header: 'Counted',
              width: 200,
              render: (c) => (
                <div>
                  <Caption1>
                    {c.countedLines} of {c.lineCount} batches
                  </Caption1>
                  <ProgressBar
                    value={c.lineCount ? c.countedLines / c.lineCount : 0}
                    aria-label={`${c.countedLines} of ${c.lineCount} counted`}
                  />
                </div>
              ),
            },
            {
              key: 'submitted',
              header: 'Submitted',
              priority: 'low',
              render: (c) => (c.submittedAt ? formatDateTime(c.submittedAt) : '—'),
            },
            {
              key: 'status',
              header: 'Status',
              render: (c) => <DocStatus status={c.status} label={STATUS_LABEL[c.status]} />,
            },
          ]}
          emptyTitle="No stock counts yet"
          emptyDescription={canCount ? 'Start a count to check the shelves against the books.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={counts.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {canCount && search.start && (
        <StartCountDialog
          defaultStoreId={search.storeId ?? working.storeId}
          onClose={() => set({ start: undefined })}
          onStarted={(id) => void navigate({ to: '/inventory/counts/$countId', params: { countId: id } })}
        />
      )}
    </Page>
  );
}

function StartCountDialog({
  defaultStoreId,
  onClose,
  onStarted,
}: {
  defaultStoreId: string | null;
  onClose: () => void;
  onStarted: (id: string) => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const working = useWorkingStore('inventory');
  const categories = useQuery(inventoryQueries.categories(true));
  const [storeId, setStoreId] = useState<string | null>(defaultStoreId);
  const [itemType, setItemType] = useState('');
  const [categoryId, setCategoryId] = useState('');
  const [remarks, setRemarks] = useState('');
  const start = useMutation({
    mutationFn: () =>
      inventoryApi.startCount({
        storeId: storeId!,
        itemType: (itemType || null) as ItemType | null,
        categoryId: categoryId || null,
        remarks: remarks.trim() || null,
      }),
    onSuccess: (result) => {
      notify.success(`Count ${result.number} started`, 'The book quantities are frozen as of now.');
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.counts });
      onStarted(result.id);
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ storeId, itemType, categoryId, remarks }),
    busy: start.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>Start a stock count</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <StorePicker stores={working.stores} value={storeId} onChange={setStoreId} required />
            <Field label="Count only" hint="Cycle counts: one item type or category at a time">
              <Select value={itemType} onChange={(_, d) => setItemType(d.value)}>
                <option value="">All item types</option>
                {ITEM_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {itemTypeLabel[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Category">
              <Select value={categoryId} onChange={(_, d) => setCategoryId(d.value)}>
                <option value="">All categories</option>
                {(categories.data ?? []).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Remarks">
              <Textarea
                value={remarks}
                rows={2}
                maxLength={1000}
                onChange={(_, d) => setRemarks(d.value)}
                placeholder="e.g. Quarterly audit, rack A to D"
              />
            </Field>
            {start.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(start.error) ? start.error.userMessage : 'Could not start the count.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={start.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!storeId || start.isPending}
              icon={start.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => start.mutate()}
            >
              Start count
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
