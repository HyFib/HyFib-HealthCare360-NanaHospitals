import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  SearchBox,
  Select,
  Spinner,
  Text,
  Textarea,
  ToggleButton,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  BinRecycleRegular,
  CheckmarkRegular,
  DeleteRegular,
  DismissRegular,
  ShieldCheckmarkRegular,
  StopRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, isApiError, useDebouncedValue, useSession } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  EmptyState,
  ErrorState,
  FilterBar,
  LoadingState,
  Page,
  PageHeader,
  Section,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  inventoryApi,
  inventoryKeys,
  inventoryQueries,
  type AdjustmentListItem,
  type ItemLookup,
} from '@/modules/inventory/api';
import { dec, sum, toNumber } from '@/modules/inventory/decimal';
import { formatExpiry, formatQty, parseNumber } from '@/modules/inventory/format';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import {
  ADJUSTMENT_REASONS,
  ADJUSTMENT_STATUSES,
  adjustmentReasonLabel,
  REASONS_NEEDING_REMARKS,
  type AdjustmentReason,
  type AdjustmentStatus,
} from '@/modules/inventory/labels';
import { LineTable } from '@/modules/inventory/LineTable';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { Detail, DetailGrid, DocStatus, ExpiryText } from '@/modules/inventory/StockBits';
import { varianceValue } from '@/modules/inventory/stockMath';
import { useWorkingStore } from '@/modules/inventory/stores';

interface AdjustmentSearch {
  status?: AdjustmentStatus | 'all';
  storeId?: string;
  reason?: AdjustmentReason;
  q?: string;
  page?: number;
  id?: string;
  create?: boolean;
}

export const Route = createFileRoute('/_app/inventory/adjustments')({
  validateSearch: (s: Record<string, unknown>): AdjustmentSearch => ({
    status:
      s.status === 'all'
        ? 'all'
        : ADJUSTMENT_STATUSES.includes(s.status as AdjustmentStatus)
          ? (s.status as AdjustmentStatus)
          : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    reason: ADJUSTMENT_REASONS.includes(s.reason as AdjustmentReason) ? (s.reason as AdjustmentReason) : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    id: typeof s.id === 'string' && s.id ? s.id : undefined,
    create: s.create === true || s.create === 'true' ? true : undefined,
  }),
  component: AdjustmentsPage,
});

const STATUS_LABEL: Record<AdjustmentStatus, string> = {
  PendingApproval: 'Waiting for approval',
  Approved: 'Approved',
  Rejected: 'Rejected',
};

/**
 * Stock corrections (INV-07): damage, breakage, expiry write-offs and count differences change stock only after a
 * second person approves them (segregation of duties).
 */
function AdjustmentsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRaise = usePermission([S.counts, S.adjustmentsApprove]);
  const working = useWorkingStore('inventory');
  const [term, setTerm] = useState(search.q ?? '');
  const [writeOffStore, setWriteOffStore] = useState<string | null>(null);
  const [confirmWriteOff, setConfirmWriteOff] = useState(false);
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const status = search.status === 'all' ? undefined : (search.status ?? 'PendingApproval');
  const adjustments = useQuery(
    inventoryQueries.adjustments({
      status,
      storeId: search.storeId,
      reason: search.reason,
      search: search.q,
      page: search.page,
    }),
  );
  const set = (patch: Partial<AdjustmentSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const writeOffStoreId = writeOffStore ?? search.storeId ?? working.storeId;

  const writeOff = useMutation({
    mutationFn: () => inventoryApi.expiredWriteOff(writeOffStoreId!),
    onSuccess: (result) => {
      setConfirmWriteOff(false);
      notify.success(
        `Write-off ${result.number} raised`,
        'Every expired batch of the store is on it. It changes stock once approved.',
      );
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.adjustments });
      set({ id: result.id, status: 'PendingApproval' });
    },
    onError: (error) => {
      setConfirmWriteOff(false);
      notify.error(error, 'Could not raise the write-off');
    },
  });

  return (
    <Page>
      <PageHeader
        title="Stock adjustments"
        subtitle="Corrections change stock only after someone other than the person who raised them approves."
        actions={
          canRaise && (
            <>
              <Button icon={<BinRecycleRegular />} onClick={() => setConfirmWriteOff(true)}>
                Write off expired stock
              </Button>
              <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ create: true })}>
                New adjustment
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
                value={search.status ?? 'PendingApproval'}
                onChange={(_, d) =>
                  set({ status: d.value === 'PendingApproval' ? undefined : (d.value as AdjustmentStatus | 'all') })
                }
              >
                {ADJUSTMENT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {STATUS_LABEL[s]}
                  </option>
                ))}
                <option value="all">All</option>
              </Select>
            </Field>
            <Field label="Reason">
              <Select
                value={search.reason ?? ''}
                onChange={(_, d) => set({ reason: (d.value || undefined) as AdjustmentReason | undefined })}
              >
                <option value="">All</option>
                {ADJUSTMENT_REASONS.map((r) => (
                  <option key={r} value={r}>
                    {adjustmentReasonLabel[r]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable<AdjustmentListItem>
          ariaLabel="Adjustments"
          rows={adjustments.data?.items}
          loading={adjustments.isLoading}
          error={adjustments.error}
          onRetry={() => void adjustments.refetch()}
          getRowId={(a) => a.id}
          onRowClick={(a) => set({ id: a.id })}
          size="small"
          columns={[
            { key: 'no', header: 'Adjustment', render: (a) => <Text font="monospace">{a.number}</Text> },
            { key: 'store', header: 'Store', render: (a) => a.storeName },
            { key: 'reason', header: 'Reason', render: (a) => adjustmentReasonLabel[a.reason] ?? a.reason },
            {
              key: 'requested',
              header: 'Raised',
              priority: 'low',
              render: (a) => (
                <span>
                  {formatDateTime(a.requestedAt)}
                  {a.requestedBy && <Caption1 block>{a.requestedBy}</Caption1>}
                </span>
              ),
            },
            { key: 'lines', header: 'Batches', align: 'end', width: 80, render: (a) => a.lineCount },
            {
              key: 'value',
              header: 'Value at cost',
              align: 'end',
              render: (a) => (
                <Text style={a.netValue < 0 ? { color: tokens.colorPaletteRedForeground1 } : undefined}>
                  {formatInr(a.netValue)}
                </Text>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              render: (a) => <DocStatus status={a.status} label={STATUS_LABEL[a.status]} />,
            },
          ]}
          emptyTitle={status === 'PendingApproval' ? 'Nothing waiting for approval' : 'No adjustments'}
          emptyDescription="Adjustments come from damage and breakage reports, expiry write-offs and stock counts."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={adjustments.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>

      {search.id && <AdjustmentDrawer adjustmentId={search.id} onClose={() => set({ id: undefined })} />}
      {canRaise && search.create && (
        <NewAdjustmentDialog
          defaultStoreId={search.storeId ?? working.storeId}
          onClose={() => set({ create: undefined })}
          onCreated={(id) => set({ create: undefined, id, status: undefined })}
        />
      )}
      <ConfirmDialog
        open={confirmWriteOff}
        title="Write off all expired stock?"
        confirmLabel="Raise write-off"
        busy={writeOff.isPending}
        onConfirm={() => writeOffStoreId && writeOff.mutate()}
        onCancel={() => setConfirmWriteOff(false)}
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <span>
            Every expired batch of the store goes on one write-off. Stock changes only when another person approves it.
          </span>
          <StorePicker stores={working.stores} value={writeOffStoreId} onChange={setWriteOffStore} label="Store" />
        </div>
      </ConfirmDialog>
    </Page>
  );
}

function AdjustmentDrawer({ adjustmentId, onClose }: { adjustmentId: string; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const me = useSession((s) => s.user?.displayName);
  const canApprove = usePermission(S.adjustmentsApprove);
  const adjustment = useQuery(inventoryQueries.adjustment(adjustmentId));
  const [confirmApprove, setConfirmApprove] = useState(false);
  const [rejecting, setRejecting] = useState(false);
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.adjustments });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.stock });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.batches });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.reports });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.counts });
  };
  const approve = useMutation({
    mutationFn: () => inventoryApi.approveAdjustment(adjustmentId),
    onSuccess: () => {
      setConfirmApprove(false);
      notify.success('Adjustment approved', 'Stock has been corrected.');
      refresh();
    },
    onError: () => setConfirmApprove(false),
  });
  const reject = useMutation({
    mutationFn: (reason: string) => inventoryApi.rejectAdjustment(adjustmentId, reason),
    onSuccess: () => {
      setRejecting(false);
      notify.success('Adjustment rejected', 'Stock was not changed.');
      refresh();
    },
  });
  const a = adjustment.data;
  const ownRequest = Boolean(a && me && a.requestedBy === me);
  const pending = a?.status === 'PendingApproval';
  const selfApprovalRefused = isApiError(approve.error) && approve.error.code === 'adjustment.self_approval';

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {a ? `Adjustment ${a.number}` : 'Adjustment'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {adjustment.isLoading && <LoadingState />}
        {adjustment.error && <ErrorState error={adjustment.error} onRetry={() => void adjustment.refetch()} />}
        {a && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <DetailGrid>
              <Detail label="Status">
                <DocStatus status={a.status} label={STATUS_LABEL[a.status]} />
              </Detail>
              <Detail label="Store">{a.storeName}</Detail>
              <Detail label="Reason">{adjustmentReasonLabel[a.reason] ?? a.reason}</Detail>
              <Detail label="Raised">
                {formatDateTime(a.requestedAt)}
                {a.requestedBy ? ` by ${a.requestedBy}` : ''}
              </Detail>
              {a.decidedAt && (
                <Detail label={a.status === 'Rejected' ? 'Rejected' : 'Approved'}>
                  {formatDateTime(a.decidedAt)}
                  {a.decidedBy ? ` by ${a.decidedBy}` : ''}
                </Detail>
              )}
              <Detail label="Net value at cost">
                <Text style={a.netValue < 0 ? { color: tokens.colorPaletteRedForeground1 } : undefined}>
                  {formatInr(a.netValue)}
                </Text>
              </Detail>
              {a.remarks && <Detail label="Remarks">{a.remarks}</Detail>}
              {a.rejectReason && <Detail label="Rejected because">{a.rejectReason}</Detail>}
            </DetailGrid>
            {pending && canApprove && (ownRequest || selfApprovalRefused) && (
              <MessageBar intent="warning" layout="multiline" icon={<ShieldCheckmarkRegular />}>
                <MessageBarBody>
                  <MessageBarTitle>Another person must approve this</MessageBarTitle>
                  You raised this adjustment, so you cannot approve it yourself. Write-offs need a second person
                  (segregation of duties). Ask another approver to open it from Stock adjustments.
                </MessageBarBody>
              </MessageBar>
            )}
            {approve.error && !selfApprovalRefused && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(approve.error) ? approve.error.userMessage : 'Not approved.'}
                </MessageBarBody>
              </MessageBar>
            )}
            <DataTable
              ariaLabel="Adjustment lines"
              rows={a.lines}
              getRowId={(l) => l.id}
              size="small"
              columns={[
                { key: 'item', header: 'Item', render: (l) => <Text weight="semibold">{l.itemName}</Text> },
                { key: 'batch', header: 'Batch', render: (l) => <Text font="monospace">{l.batchNumber}</Text> },
                { key: 'exp', header: 'Expiry', render: (l) => <ExpiryText date={l.expiryDate} /> },
                {
                  key: 'book',
                  header: 'Book qty',
                  align: 'end',
                  priority: 'low',
                  render: (l) => formatQty(l.systemQuantity),
                },
                {
                  key: 'change',
                  header: 'Change',
                  align: 'end',
                  render: (l) => (
                    <Text
                      weight="semibold"
                      style={{
                        color:
                          l.quantityChange < 0
                            ? tokens.colorPaletteRedForeground1
                            : tokens.colorPaletteGreenForeground1,
                      }}
                    >
                      {l.quantityChange > 0 ? '+' : ''}
                      {formatQty(l.quantityChange)}
                    </Text>
                  ),
                },
                {
                  key: 'cost',
                  header: 'Unit cost',
                  align: 'end',
                  priority: 'low',
                  render: (l) => formatInr(l.unitCost),
                },
                { key: 'value', header: 'Value', align: 'end', render: (l) => formatInr(l.value) },
              ]}
            />
          </div>
        )}
      </DrawerBody>
      <DrawerFooter>
        {a && pending && canApprove && (
          <>
            <Button icon={<StopRegular />} onClick={() => setRejecting(true)}>
              Reject
            </Button>
            <Button
              appearance="primary"
              icon={<CheckmarkRegular />}
              disabled={ownRequest || selfApprovalRefused}
              onClick={() => setConfirmApprove(true)}
            >
              Approve and change stock
            </Button>
          </>
        )}
        {a && pending && !canApprove && <Caption1>Waiting for an approver (hospital administrator).</Caption1>}
      </DrawerFooter>
      <ConfirmDialog
        open={confirmApprove}
        title="Approve this adjustment?"
        confirmLabel="Approve"
        busy={approve.isPending}
        onConfirm={() => approve.mutate()}
        onCancel={() => setConfirmApprove(false)}
      >
        The stock of {a?.lines.length ?? 0} batches changes now ({formatInr(a?.netValue)} at cost). This cannot be
        undone except with another adjustment.
      </ConfirmDialog>
      {rejecting && a && (
        <ReasonDialog
          title={`Reject adjustment ${a.number}?`}
          confirmLabel="Reject"
          destructive
          busy={reject.isPending}
          error={reject.error}
          onSubmit={(reason) => reject.mutate(reason)}
          onClose={() => setRejecting(false)}
        >
          Stock is not changed. The person who raised it sees your reason.
        </ReasonDialog>
      )}
    </OverlayDrawer>
  );
}

interface AdjustLine {
  key: string;
  item: ItemLookup;
  batchId: string;
  direction: 'remove' | 'add';
  quantity: string;
}

function NewAdjustmentDialog({
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
  const working = useWorkingStore('inventory');
  const [storeId, setStoreId] = useState<string | null>(defaultStoreId);
  const [reason, setReason] = useState<AdjustmentReason>('Damage');
  const [remarks, setRemarks] = useState('');
  const [lines, setLines] = useState<AdjustLine[]>([]);
  const itemIds = Array.from(new Set(lines.map((l) => l.item.id)));
  const batchResults = useQueries({
    queries: itemIds.map((id) => ({
      ...inventoryQueries.batches(storeId ?? '', id, false),
      enabled: Boolean(storeId),
    })),
  });
  const batchesOf = (itemId: string) => batchResults[itemIds.indexOf(itemId)]?.data;

  const planned = lines.map((line) => {
    const batch = batchesOf(line.item.id)?.find((b) => b.batchId === line.batchId);
    const quantity = parseNumber(line.quantity) ?? 0;
    const change = line.direction === 'remove' ? -quantity : quantity;
    const problem = !line.batchId
      ? 'Choose the batch.'
      : !(quantity > 0)
        ? 'Enter the quantity.'
        : batch && line.direction === 'remove' && quantity > batch.available
          ? `Only ${formatQty(batch.available)} free in this batch.`
          : null;
    return { line, batch, change, value: batch ? varianceValue(change, batch.unitCost) : 0, problem };
  });
  const duplicate = new Set(lines.map((l) => l.batchId).filter(Boolean)).size !== lines.filter((l) => l.batchId).length;
  const needsRemarks = REASONS_NEEDING_REMARKS.includes(reason);
  const valid =
    Boolean(storeId) &&
    planned.length > 0 &&
    planned.every((p) => !p.problem) &&
    !duplicate &&
    (!needsRemarks || remarks.trim().length > 0);
  const total = toNumber(sum(planned.map((p) => dec(p.value))));

  const create = useMutation({
    mutationFn: () =>
      inventoryApi.createAdjustment({
        storeId: storeId!,
        reason,
        remarks: remarks.trim() || null,
        lines: planned.map((p) => ({ batchId: p.line.batchId, quantityChange: p.change })),
      }),
    onSuccess: (result) => {
      notify.success(`Adjustment ${result.number} raised`, 'It changes stock once another person approves it.');
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.adjustments });
      onCreated(result.id);
    },
  });

  const add = (item: ItemLookup) =>
    setLines([
      ...lines,
      { key: `${item.id}-${lines.length}-${item.code}`, item, batchId: '', direction: 'remove', quantity: '' },
    ]);
  const update = (key: string, patch: Partial<AdjustLine>) =>
    setLines(lines.map((l) => (l.key === key ? { ...l, ...patch } : l)));

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ storeId, reason, remarks, lines }),
    busy: create.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 960, width: '96vw' }}>
        <DialogBody>
          <DialogTitle>New stock adjustment</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))', gap: 12 }}>
              <StorePicker
                stores={working.stores}
                value={storeId}
                onChange={(id) => {
                  setStoreId(id);
                  setLines([]);
                }}
                required
              />
              <Field label="Reason" required>
                <Select value={reason} onChange={(_, d) => setReason(d.value as AdjustmentReason)}>
                  {ADJUSTMENT_REASONS.filter((r) => r !== 'CountVariance').map((r) => (
                    <option key={r} value={r}>
                      {adjustmentReasonLabel[r]}
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
            <ItemPicker
              storeId={storeId}
              onPick={add}
              label="Add item"
              hint="Then choose the batch that is damaged, lost or found"
            />
            <LineTable
              ariaLabel="Adjustment lines"
              rows={planned}
              getRowId={(p) => p.line.key}
              isFlagged={(p) => p.problem !== null && p.line.quantity !== ''}
              empty={
                <EmptyState title="No batches yet" description="Search above for the item, then pick the batch." />
              }
              columns={[
                { key: 'item', header: 'Item', render: (p) => <Text weight="semibold">{p.line.item.name}</Text> },
                {
                  key: 'batch',
                  header: 'Batch',
                  render: (p) => (
                    <Select
                      aria-label={`Batch of ${p.line.item.name}`}
                      value={p.line.batchId}
                      onChange={(_, d) => update(p.line.key, { batchId: d.value })}
                    >
                      <option value="">Choose batch</option>
                      {(batchesOf(p.line.item.id) ?? []).map((b) => (
                        <option key={b.batchId} value={b.batchId}>
                          {b.batchNumber} · exp {formatExpiry(b.expiryDate)} · {formatQty(b.onHand)} on hand
                          {b.isExpired ? ' · expired' : ''}
                          {b.isBlocked ? ' · on hold' : ''}
                        </option>
                      ))}
                    </Select>
                  ),
                },
                {
                  key: 'dir',
                  header: 'Change',
                  render: (p) => (
                    <span style={{ display: 'inline-flex', gap: 4 }}>
                      <ToggleButton
                        size="small"
                        checked={p.line.direction === 'remove'}
                        onClick={() => update(p.line.key, { direction: 'remove' })}
                      >
                        Remove
                      </ToggleButton>
                      <ToggleButton
                        size="small"
                        checked={p.line.direction === 'add'}
                        onClick={() => update(p.line.key, { direction: 'add' })}
                      >
                        Add
                      </ToggleButton>
                    </span>
                  ),
                },
                {
                  key: 'qty',
                  header: 'Quantity',
                  width: 130,
                  render: (p) => (
                    <Input
                      type="number"
                      min={0}
                      aria-label={`Quantity of ${p.line.item.name}`}
                      value={p.line.quantity}
                      contentAfter={<Caption1>{p.line.item.baseUnit}</Caption1>}
                      onChange={(_, d) => update(p.line.key, { quantity: d.value })}
                    />
                  ),
                },
                {
                  key: 'value',
                  header: 'Value',
                  align: 'end',
                  render: (p) => (
                    <span>
                      {p.batch ? formatInr(p.value) : '—'}
                      {p.problem && p.line.quantity !== '' && (
                        <Caption1 block style={{ color: tokens.colorPaletteRedForeground1 }}>
                          {p.problem}
                        </Caption1>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'remove',
                  header: '',
                  width: 44,
                  render: (p) => (
                    <Button
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      aria-label="Remove line"
                      onClick={() => setLines(lines.filter((l) => l.key !== p.line.key))}
                    />
                  ),
                },
              ]}
            />
            <Field
              label="Remarks"
              required={needsRemarks}
              hint={needsRemarks ? 'Explain what happened; the approver reads this.' : undefined}
            >
              <Textarea value={remarks} rows={2} maxLength={1000} onChange={(_, d) => setRemarks(d.value)} />
            </Field>
            {duplicate && (
              <MessageBar intent="warning">
                <MessageBarBody>Each batch can appear only once.</MessageBarBody>
              </MessageBar>
            )}
            {planned.length > 0 && (
              <Caption1>
                Net effect at cost: <b>{formatInr(total)}</b>. Stock changes only after approval by another person.
              </Caption1>
            )}
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
              icon={create.isPending ? <Spinner size="tiny" /> : undefined}
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
