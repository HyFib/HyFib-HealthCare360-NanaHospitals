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
  OverlayDrawer,
  SearchBox,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { ArrowSwapRegular, BoxCheckmarkRegular, DeleteRegular, DismissCircleRegular, DismissRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, isApiError, useDebouncedValue } from '@hc360/core';
import { ConfirmDialog, DataTable, EmptyState, ErrorState, FilterBar, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { inventoryApi, inventoryKeys, inventoryQueries, type ItemLookup, type TransferListItem } from '@/modules/inventory/api';
import { allocateFefo, allocateFromBatch } from '@/modules/inventory/fefo';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import { TRANSFER_STATUSES, type TransferStatus } from '@/modules/inventory/labels';
import { LineTable } from '@/modules/inventory/LineTable';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { printTransferNote } from '@/modules/inventory/print';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { Detail, DetailGrid, DocStatus, ExpiryText, ItemFlags } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';
import { BatchPick } from '@/modules/pharmacy/BatchPick';
import { useLetterhead } from '@/modules/pharmacy/counter';

interface TransferSearch {
  status?: TransferStatus;
  storeId?: string;
  q?: string;
  page?: number;
  id?: string;
  send?: boolean;
}

export const Route = createFileRoute('/_app/inventory/transfers')({
  validateSearch: (s: Record<string, unknown>): TransferSearch => ({
    status: TRANSFER_STATUSES.includes(s.status as TransferStatus) ? (s.status as TransferStatus) : undefined,
    storeId: typeof s.storeId === 'string' && s.storeId ? s.storeId : undefined,
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    id: typeof s.id === 'string' && s.id ? s.id : undefined,
    send: s.send === true || s.send === 'true' ? true : undefined,
  }),
  component: TransfersPage,
});

const STATUS_LABEL: Record<TransferStatus, string> = { InTransit: 'In transit', Received: 'Received', Cancelled: 'Cancelled' };

/** Stock transfer notes between stores (INV-06, PH-04): goods leave the source on dispatch and arrive when received. */
function TransfersPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canTransfer = usePermission(S.transfers);
  const working = useWorkingStore('inventory');
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const transfers = useQuery(inventoryQueries.transfers({ status: search.status, storeId: search.storeId, search: search.q, page: search.page }));
  const set = (patch: Partial<TransferSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Transfers"
        subtitle="Stock moved between stores keeps its batch, expiry and MRP. Goods stay in transit until the receiving store confirms."
        actions={
          canTransfer && (
            <Button appearance="primary" icon={<ArrowSwapRegular />} onClick={() => set({ send: true })}>
              Send stock
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Transfer number" style={{ flex: '0 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Exact number" />
            </Field>
            <StorePicker stores={working.stores} value={search.storeId ?? null} onChange={(id) => set({ storeId: id ?? undefined })} allowAll label="Store (sending or receiving)" />
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as TransferStatus | undefined })}>
                <option value="">All</option>
                {TRANSFER_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable<TransferListItem>
          ariaLabel="Transfers"
          rows={transfers.data?.items}
          loading={transfers.isLoading}
          error={transfers.error}
          onRetry={() => void transfers.refetch()}
          getRowId={(t) => t.id}
          onRowClick={(t) => void navigate({ search: (prev) => ({ ...prev, id: t.id }) })}
          size="small"
          columns={[
            { key: 'no', header: 'Transfer', render: (t) => <Text font="monospace">{t.number}</Text> },
            { key: 'route', header: 'From → to', render: (t) => `${t.fromStoreName} → ${t.toStoreName}` },
            { key: 'sent', header: 'Sent', render: (t) => formatDateTime(t.dispatchedAt) },
            { key: 'received', header: 'Received', priority: 'low', render: (t) => (t.receivedAt ? formatDateTime(t.receivedAt) : '—') },
            { key: 'lines', header: 'Lines', align: 'end', width: 70, render: (t) => t.lineCount },
            { key: 'value', header: 'Value', align: 'end', priority: 'low', render: (t) => formatInr(t.totalValue) },
            { key: 'status', header: 'Status', render: (t) => <DocStatus status={t.status} label={STATUS_LABEL[t.status]} /> },
          ]}
          emptyTitle="No transfers"
          emptyDescription={canTransfer ? 'Send stock to move items between the central store, pharmacies and sub-stores.' : undefined}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={transfers.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {search.id && <TransferDrawer transferId={search.id} onClose={() => set({ id: undefined })} />}
      {canTransfer && search.send && <SendDialog onClose={() => set({ send: undefined })} onSent={(id) => set({ send: undefined, id })} />}
    </Page>
  );
}

function TransferDrawer({ transferId, onClose }: { transferId: string; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canTransfer = usePermission(S.transfers);
  const transfer = useQuery(inventoryQueries.transfer(transferId));
  const [confirmReceive, setConfirmReceive] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.transfers });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.stock });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.batches });
  };
  const receive = useMutation({
    mutationFn: () => inventoryApi.receiveTransfer(transferId),
    onSuccess: () => {
      notify.success('Transfer received', 'The stock is now in the receiving store.');
      setConfirmReceive(false);
      refresh();
    },
    onError: (error) => {
      setConfirmReceive(false);
      notify.error(error, 'Could not receive the transfer');
    },
  });
  const cancel = useMutation({
    mutationFn: (reason: string) => inventoryApi.cancelTransfer(transferId, reason),
    onSuccess: () => {
      notify.success('Transfer cancelled', 'The stock went back to the batches it came from.');
      setCancelling(false);
      refresh();
    },
  });
  const t = transfer.data;
  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>
          {t ? `Transfer ${t.number}` : 'Transfer'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {transfer.isLoading && <LoadingState />}
        {transfer.error && <ErrorState error={transfer.error} onRetry={() => void transfer.refetch()} />}
        {t && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <DetailGrid>
              <Detail label="Status">
                <DocStatus status={t.status} label={STATUS_LABEL[t.status]} />
              </Detail>
              <Detail label="From">{t.fromStoreName}</Detail>
              <Detail label="To">{t.toStoreName}</Detail>
              <Detail label="Sent">{formatDateTime(t.dispatchedAt)}</Detail>
              <Detail label="Received">{t.receivedAt ? formatDateTime(t.receivedAt) : null}</Detail>
              <Detail label="Value at cost">{formatInr(t.totalValue)}</Detail>
              {t.remarks && <Detail label="Remarks">{t.remarks}</Detail>}
              {t.cancelReason && <Detail label="Cancelled because">{t.cancelReason}</Detail>}
            </DetailGrid>
            <DataTable
              ariaLabel="Transfer lines"
              rows={t.lines}
              getRowId={(l) => l.id}
              size="small"
              columns={[
                { key: 'item', header: 'Item', render: (l) => <Text weight="semibold">{l.itemName}</Text> },
                { key: 'batch', header: 'Batch', render: (l) => <Text font="monospace">{l.batchNumber}</Text> },
                { key: 'exp', header: 'Expiry', render: (l) => <ExpiryText date={l.expiryDate} /> },
                { key: 'qty', header: 'Qty', align: 'end', render: (l) => formatQty(l.quantity) },
                { key: 'mrp', header: 'MRP', align: 'end', priority: 'low', render: (l) => formatInr(l.mrp) },
                { key: 'rate', header: 'Cost', align: 'end', priority: 'low', render: (l) => formatInr(l.purchaseRate) },
              ]}
            />
          </div>
        )}
      </DrawerBody>
      <DrawerFooter>
        {t && (
          <>
            <Button icon={<PrintRegular />} onClick={() => void printTransferNote(t, letterhead).catch((error: unknown) => notify.error(error, 'Printing failed'))}>
              Print note
            </Button>
            {canTransfer && t.status === 'InTransit' && (
              <>
                <Button icon={<DismissCircleRegular />} onClick={() => setCancelling(true)}>
                  Cancel transfer
                </Button>
                <Button appearance="primary" icon={<BoxCheckmarkRegular />} onClick={() => setConfirmReceive(true)}>
                  Receive
                </Button>
              </>
            )}
          </>
        )}
      </DrawerFooter>
      <ConfirmDialog
        open={confirmReceive}
        title="Receive this transfer?"
        confirmLabel="Receive"
        busy={receive.isPending}
        onConfirm={() => receive.mutate()}
        onCancel={() => setConfirmReceive(false)}
      >
        Check the goods against the note first. The same batches, expiry dates and MRPs are booked into {t?.toStoreName}.
      </ConfirmDialog>
      {cancelling && (
        <ReasonDialog
          title="Cancel this transfer?"
          confirmLabel="Cancel transfer"
          destructive
          busy={cancel.isPending}
          error={cancel.error}
          onSubmit={(reason) => cancel.mutate(reason)}
          onClose={() => setCancelling(false)}
        >
          The stock goes back to the exact batches it left from in {t?.fromStoreName}.
        </ReasonDialog>
      )}
    </OverlayDrawer>
  );
}

interface SendLine {
  item: ItemLookup;
  quantity: string;
  batchId: string | null;
}

function SendDialog({ onClose, onSent }: { onClose: () => void; onSent: (id: string) => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const working = useWorkingStore('inventory');
  const [fromStoreId, setFromStoreId] = useState<string | null>(null);
  const [toStoreId, setToStoreId] = useState<string | null>(null);
  const [remarks, setRemarks] = useState('');
  const [lines, setLines] = useState<SendLine[]>([]);
  const sourceId = fromStoreId ?? working.storeId;
  const batchQueries = useQueries({ queries: lines.map((l) => ({ ...inventoryQueries.batches(sourceId ?? '', l.item.id), enabled: Boolean(sourceId) })) });

  const planned = lines.map((line, index) => {
    const batches = batchQueries[index]?.data;
    const quantity = parseNumber(line.quantity) ?? 0;
    const picked = line.batchId ? batches?.find((b) => b.batchId === line.batchId) : undefined;
    const allocation = batches ? (picked ? allocateFromBatch(quantity, picked) : allocateFefo(quantity, batches)) : null;
    const problem = !(quantity > 0) ? 'Enter the quantity.' : allocation && allocation.shortfall > 0 ? `Only ${formatQty(allocation.allocated, line.item.baseUnit)} can be sent.` : null;
    return { line, batches, quantity, allocation, problem };
  });
  const valid = Boolean(sourceId) && Boolean(toStoreId) && toStoreId !== sourceId && planned.length > 0 && planned.every((p) => !p.problem && p.allocation);

  const add = (item: ItemLookup) => {
    if (lines.some((l) => l.item.id === item.id)) {
      notify.info(`${item.name} is already on the list`);
      return;
    }
    setLines([...lines, { item, quantity: '', batchId: null }]);
  };
  const update = (id: string, patch: Partial<SendLine>) => setLines(lines.map((l) => (l.item.id === id ? { ...l, ...patch } : l)));

  const send = useMutation({
    mutationFn: () =>
      inventoryApi.dispatchTransfer({
        fromStoreId: sourceId!,
        toStoreId: toStoreId!,
        remarks: remarks.trim() || null,
        lines: planned.map((p) => ({ itemId: p.line.item.id, quantity: p.quantity, batchId: p.line.batchId })),
      }),
    onSuccess: (result) => {
      notify.success(`Transfer ${result.number} sent`, 'The stock is in transit until the receiving store confirms.');
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.transfers });
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.stock });
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.batches });
      onSent(result.id);
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !send.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 960, width: '96vw' }}>
        <DialogBody>
          <DialogTitle>Send stock to another store</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))', gap: 12 }}>
              <StorePicker
                stores={working.stores}
                value={sourceId}
                onChange={(id) => {
                  setFromStoreId(id);
                  setLines(lines.map((l) => ({ ...l, batchId: null })));
                }}
                label="From"
                required
              />
              <StorePicker stores={working.stores} value={toStoreId} onChange={setToStoreId} excludeId={sourceId} label="To" required />
            </div>
            <ItemPicker storeId={sourceId} onPick={add} label="Add item" hint="Stock shown is what the sending store has" />
            <LineTable
              ariaLabel="Items to send"
              rows={planned}
              getRowId={(p) => p.line.item.id}
              isFlagged={(p) => p.problem !== null && p.quantity > 0}
              empty={<EmptyState title="No items yet" description="Search above to add what is being sent." />}
              columns={[
                {
                  key: 'item',
                  header: 'Item',
                  render: (p) => (
                    <span>
                      <Text weight="semibold">{p.line.item.name}</Text> <ItemFlags schedule={p.line.item.schedule} isControlled={p.line.item.isControlled} isColdChain={p.line.item.isColdChain} />
                      {p.problem && p.quantity > 0 && (
                        <Caption1 block style={{ color: tokens.colorPaletteRedForeground1 }}>
                          {p.problem}
                        </Caption1>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'qty',
                  header: 'Quantity',
                  width: 150,
                  render: (p) => (
                    <Input
                      type="number"
                      min={0}
                      aria-label={`Quantity of ${p.line.item.name}`}
                      value={p.line.quantity}
                      contentAfter={<Caption1>{p.line.item.baseUnit}</Caption1>}
                      onChange={(_, d) => update(p.line.item.id, { quantity: d.value })}
                    />
                  ),
                },
                {
                  key: 'batch',
                  header: 'Batches',
                  render: (p) => (
                    <BatchPick
                      batches={p.batches}
                      allocations={p.allocation?.allocations ?? []}
                      pickedBatchId={p.line.batchId}
                      onPick={(batchId) => update(p.line.item.id, { batchId })}
                      unit={p.line.item.baseUnit}
                    />
                  ),
                },
                {
                  key: 'remove',
                  header: '',
                  width: 44,
                  render: (p) => <Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Remove ${p.line.item.name}`} onClick={() => setLines(lines.filter((l) => l.item.id !== p.line.item.id))} />,
                },
              ]}
            />
            <Field label="Remarks">
              <Textarea value={remarks} rows={2} maxLength={500} onChange={(_, d) => setRemarks(d.value)} />
            </Field>
            {send.error && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(send.error) ? send.error.userMessage : 'Nothing was sent.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={send.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || send.isPending} icon={send.isPending ? <Spinner size="tiny" /> : undefined} onClick={() => send.mutate()}>
              Send
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

