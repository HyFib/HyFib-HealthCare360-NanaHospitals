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
  Input,
  MessageBar,
  MessageBarBody,
  SearchBox,
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, DataTrendingRegular, DeleteRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, isApiError, todayIso, useDebouncedValue } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  EmptyState,
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
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import type { ItemLookup } from '@/modules/inventory/api';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import { LineTable } from '@/modules/inventory/LineTable';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { DocStatus } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';
import {
  purchaseApi,
  purchaseKeys,
  purchaseQueries,
  REQUISITION_STATUSES,
  type RequisitionListItem,
  type RequisitionStatus,
} from '@/modules/purchase/api';

interface RequisitionSearch {
  status?: RequisitionStatus;
  storeId?: string;
  q?: string;
  page?: number;
  create?: boolean;
}

export const Route = createFileRoute('/_app/purchase/requisitions/')({
  validateSearch: (s: Record<string, unknown>): RequisitionSearch => ({
    status: REQUISITION_STATUSES.includes(s.status as RequisitionStatus) ? (s.status as RequisitionStatus) : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    create: s.create === true || s.create === 'true' ? true : undefined,
  }),
  component: RequisitionsPage,
});

const STATUS_LABEL: Record<RequisitionStatus, string> = {
  Submitted: 'Waiting for approval',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Ordered: 'Ordered',
  Cancelled: 'Cancelled',
};

/** Purchase requisitions: a store asks to buy; an approver agrees; purchase turns it into an order. */
function RequisitionsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(S.purchaseManage);
  const working = useWorkingStore('purchase');
  const [term, setTerm] = useState(search.q ?? '');
  const [reorderStore, setReorderStore] = useState<string | null>(null);
  const [confirmReorder, setConfirmReorder] = useState(false);
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const requisitions = useQuery(
    purchaseQueries.requisitions({
      status: search.status,
      storeId: search.storeId,
      search: search.q,
      page: search.page,
    }),
  );
  const set = (patch: Partial<RequisitionSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const reorderStoreId = reorderStore ?? search.storeId ?? working.storeId;

  const fromReorder = useMutation({
    mutationFn: () => purchaseApi.requisitionFromReorder(reorderStoreId!),
    onSuccess: (result) => {
      setConfirmReorder(false);
      notify.success(`Requisition ${result.number} created from the reorder list`);
      void queryClient.invalidateQueries({ queryKey: purchaseKeys.all });
      void navigate({ to: '/purchase/requisitions/$requisitionId', params: { requisitionId: result.id } });
    },
    onError: (error) => {
      setConfirmReorder(false);
      notify.error(error, 'Could not create the requisition');
    },
  });

  return (
    <Page>
      <PageHeader
        title="Purchase requisitions"
        subtitle="Requests to buy, in purchase units. Approved requisitions become purchase orders."
        actions={
          canManage && (
            <>
              <Button icon={<DataTrendingRegular />} onClick={() => setConfirmReorder(true)}>
                From the reorder list
              </Button>
              <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ create: true })}>
                New requisition
              </Button>
            </>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Number" style={{ flex: '0 1 200px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Exact number" />
            </Field>
            <StorePicker
              stores={working.stores}
              value={search.storeId ?? null}
              onChange={(id) => set({ storeId: id ?? undefined })}
              allowAll
            />
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as RequisitionStatus | undefined })}
              >
                <option value="">All</option>
                {REQUISITION_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable<RequisitionListItem>
          ariaLabel="Requisitions"
          rows={requisitions.data?.items}
          loading={requisitions.isLoading}
          error={requisitions.error}
          onRetry={() => void requisitions.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) =>
            void navigate({ to: '/purchase/requisitions/$requisitionId', params: { requisitionId: r.id } })
          }
          size="small"
          columns={[
            { key: 'no', header: 'Requisition', render: (r) => <Text font="monospace">{r.number}</Text> },
            { key: 'store', header: 'For store', render: (r) => r.storeName },
            { key: 'needed', header: 'Needed by', render: (r) => (r.requiredBy ? formatDateOnly(r.requiredBy) : '—') },
            { key: 'lines', header: 'Items', align: 'end', width: 70, render: (r) => r.lineCount },
            {
              key: 'raised',
              header: 'Raised',
              priority: 'low',
              render: (r) => (
                <span>
                  {formatDateTime(r.createdAt)}
                  {r.requestedBy && <Caption1 block>{r.requestedBy}</Caption1>}
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              render: (r) => <DocStatus status={r.status} label={STATUS_LABEL[r.status]} />,
            },
          ]}
          emptyTitle="No requisitions"
          emptyDescription={canManage ? 'Raise one, or create it straight from the reorder list.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={requisitions.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {canManage && search.create && (
        <NewRequisitionDialog
          defaultStoreId={search.storeId ?? working.storeId}
          onClose={() => set({ create: undefined })}
          onCreated={(id) =>
            void navigate({ to: '/purchase/requisitions/$requisitionId', params: { requisitionId: id } })
          }
        />
      )}
      <ConfirmDialog
        open={confirmReorder}
        title="Create a requisition from the reorder list?"
        confirmLabel="Create requisition"
        busy={fromReorder.isPending}
        onConfirm={() => reorderStoreId && fromReorder.mutate()}
        onCancel={() => setConfirmReorder(false)}
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <span>
            Every item at or below its reorder level (after what is already on order) goes on one requisition, in
            purchase units.
          </span>
          <StorePicker stores={working.stores} value={reorderStoreId} onChange={setReorderStore} label="For store" />
        </div>
      </ConfirmDialog>
    </Page>
  );
}

interface RequisitionLine {
  item: ItemLookup;
  quantity: string;
  remarks: string;
}

function NewRequisitionDialog({
  defaultStoreId,
  onClose,
  onCreated,
}: {
  defaultStoreId: string | null;
  onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const working = useWorkingStore('purchase');
  const [storeId, setStoreId] = useState<string | null>(defaultStoreId);
  const [requiredBy, setRequiredBy] = useState('');
  const [remarks, setRemarks] = useState('');
  const [lines, setLines] = useState<RequisitionLine[]>([]);
  const invalid = lines.some((l) => !((parseNumber(l.quantity) ?? 0) > 0));
  const valid = Boolean(storeId) && lines.length > 0 && !invalid;
  const create = useMutation({
    mutationFn: () =>
      purchaseApi.createRequisition({
        storeId: storeId!,
        requiredBy: requiredBy || null,
        remarks: remarks.trim() || null,
        lines: lines.map((l) => ({
          itemId: l.item.id,
          quantity: parseNumber(l.quantity) ?? 0,
          remarks: l.remarks.trim() || null,
        })),
      }),
    onSuccess: (result) => {
      notify.success(`Requisition ${result.number} sent for approval`);
      void queryClient.invalidateQueries({ queryKey: purchaseKeys.all });
      onCreated(result.id);
    },
  });
  const update = (id: string, patch: Partial<RequisitionLine>) =>
    setLines(lines.map((l) => (l.item.id === id ? { ...l, ...patch } : l)));
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ storeId, requiredBy, remarks, lines }),
    busy: create.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 880, width: '96vw' }}>
        <DialogBody>
          <DialogTitle>New purchase requisition</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 12 }}>
              <StorePicker stores={working.stores} value={storeId} onChange={setStoreId} label="For store" required />
              <Field label="Needed by">
                <Input type="date" min={todayIso()} value={requiredBy} onChange={(_, d) => setRequiredBy(d.value)} />
              </Field>
            </div>
            <ItemPicker
              storeId={storeId}
              label="Add item"
              onPick={(item) =>
                lines.some((l) => l.item.id === item.id)
                  ? notify.info(`${item.name} is already listed`)
                  : setLines([...lines, { item, quantity: '', remarks: '' }])
              }
            />
            <LineTable
              ariaLabel="Requisition lines"
              rows={lines}
              getRowId={(l) => l.item.id}
              empty={<EmptyState title="No items yet" description="Search above to add what the store needs to buy." />}
              columns={[
                {
                  key: 'item',
                  header: 'Item',
                  render: (l) => (
                    <span>
                      <Text weight="semibold">{l.item.name}</Text>
                      <Caption1 block>
                        {l.item.available !== null
                          ? `${formatQty(l.item.available, l.item.baseUnit)} in stock`
                          : l.item.code}
                        {l.item.purchaseUnitFactor !== 1
                          ? ` · 1 ${l.item.purchaseUnit} = ${formatQty(l.item.purchaseUnitFactor, l.item.baseUnit)}`
                          : ''}
                      </Caption1>
                    </span>
                  ),
                },
                {
                  key: 'qty',
                  header: 'Quantity',
                  width: 170,
                  render: (l) => (
                    <Input
                      type="number"
                      min={0}
                      aria-label={`Quantity of ${l.item.name}`}
                      value={l.quantity}
                      contentAfter={<Caption1>{l.item.purchaseUnit}</Caption1>}
                      onChange={(_, d) => update(l.item.id, { quantity: d.value })}
                    />
                  ),
                },
                {
                  key: 'remarks',
                  header: 'Note',
                  priority: 'low',
                  render: (l) => (
                    <Input
                      value={l.remarks}
                      maxLength={300}
                      aria-label={`Note for ${l.item.name}`}
                      onChange={(_, d) => update(l.item.id, { remarks: d.value })}
                    />
                  ),
                },
                {
                  key: 'remove',
                  header: '',
                  width: 44,
                  render: (l) => (
                    <Button
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      aria-label={`Remove ${l.item.name}`}
                      onClick={() => setLines(lines.filter((x) => x.item.id !== l.item.id))}
                    />
                  ),
                },
              ]}
            />
            <Field label="Remarks">
              <Textarea value={remarks} rows={2} maxLength={1000} onChange={(_, d) => setRemarks(d.value)} />
            </Field>
            {create.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(create.error) ? create.error.userMessage : 'Not saved.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={create.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!valid || create.isPending}
              icon={create.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => create.mutate()}
            >
              Send for approval
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
