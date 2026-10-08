import {
  Badge,
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  OverlayDrawer,
  Subtitle2,
  Switch,
  Text,
  Tooltip,
} from '@fluentui/react-components';
import { DismissRegular, HistoryRegular, LockClosedRegular, LockOpenRegular, TagRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr } from '@hc360/core';
import { ConfirmDialog, DataTable, EmptyState, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { PrintTagsDialog } from '@/modules/pharmacy/PrintTagsDialog';
import { inventoryApi, inventoryKeys, inventoryQueries, type BatchStock } from './api';
import { formatPercent, formatQty } from './format';
import { referenceModuleLabel } from './labels';
import { BATCH_READERS, SUPPLY_PERMISSIONS as S } from './permissions';
import { ReasonDialog } from './ReasonDialog';
import { ExpiryText } from './StockBits';

/**
 * Batch drill-down for one item in one store: FEFO order, expiry, MRP and cost, reservations, and quality holds.
 * A blocked batch is never sold, dispensed or issued until it is released.
 */
export function BatchDrawer({
  storeId,
  storeName,
  itemId,
  itemCode,
  itemName,
  baseUnit,
  onClose,
}: {
  storeId: string;
  storeName: string;
  itemId: string;
  /** For pharmacy tags (INT-BAR); without it no tags are offered. */
  itemCode?: string;
  itemName: string;
  baseUnit?: string;
  onClose: () => void;
}) {
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canHold = usePermission([S.itemsManage, S.adjustmentsApprove]);
  const canRead = usePermission(BATCH_READERS);
  const canSeeLedger = usePermission(S.stockRead);
  const [includeEmpty, setIncludeEmpty] = useState(false);
  const [blocking, setBlocking] = useState<BatchStock | null>(null);
  const [releasing, setReleasing] = useState<BatchStock | null>(null);
  const [tagging, setTagging] = useState<BatchStock | null>(null);
  const batches = useQuery({ ...inventoryQueries.batches(storeId, itemId, includeEmpty), enabled: canRead });
  const reservations = useQuery({ ...inventoryQueries.reservations(storeId, itemId), enabled: canRead });

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.batches });
    void queryClient.invalidateQueries({ queryKey: inventoryKeys.stock });
    void queryClient.invalidateQueries({ queryKey: ['pharmacy', 'stock'] });
  };

  const block = useMutation({
    mutationFn: ({ batch, reason }: { batch: BatchStock; reason: string }) =>
      inventoryApi.blockBatch(batch.batchId, reason),
    onSuccess: (_, { batch }) => {
      notify.success(
        `Batch ${batch.batchNumber} is on hold`,
        'It will not be sold, dispensed or issued until released.',
      );
      setBlocking(null);
      refresh();
    },
  });
  const release = useMutation({
    mutationFn: (batch: BatchStock) => inventoryApi.unblockBatch(batch.batchId),
    onSuccess: (_, batch) => {
      notify.success(`Batch ${batch.batchNumber} released`);
      setReleasing(null);
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not release the batch'),
  });

  const unit = baseUnit ?? '';

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {itemName}
        </DrawerHeaderTitle>
        <Caption1>Batches in {storeName}, in the order they are used (earliest expiry first)</Caption1>
      </DrawerHeader>
      <DrawerBody>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          <Switch
            checked={includeEmpty}
            onChange={(_, d) => setIncludeEmpty(d.checked)}
            label="Show finished batches"
          />
          {!canRead && (
            <EmptyState
              title="Your account cannot see batch details"
              description="Ask the administrator for the permission to view stock."
            />
          )}
          {canRead && (
            <DataTable<BatchStock>
              ariaLabel={`Batches of ${itemName}`}
              rows={batches.data}
              loading={batches.isLoading}
              error={batches.error}
              onRetry={() => void batches.refetch()}
              getRowId={(b) => b.batchId}
              size="small"
              columns={[
                { key: 'batch', header: 'Batch', render: (b) => <Text font="monospace">{b.batchNumber}</Text> },
                {
                  key: 'expiry',
                  header: 'Expiry',
                  render: (b) => <ExpiryText date={b.expiryDate} daysToExpiry={b.daysToExpiry} showDays />,
                },
                { key: 'onHand', header: 'On hand', align: 'end', render: (b) => formatQty(b.onHand, unit) },
                {
                  key: 'reserved',
                  header: 'Reserved',
                  align: 'end',
                  priority: 'low',
                  render: (b) => (b.reserved ? formatQty(b.reserved) : '—'),
                },
                {
                  key: 'available',
                  header: 'Free',
                  align: 'end',
                  render: (b) => <Text weight="semibold">{formatQty(b.available)}</Text>,
                },
                { key: 'mrp', header: 'MRP', align: 'end', render: (b) => formatInr(b.mrp) },
                { key: 'cost', header: 'Cost', align: 'end', priority: 'low', render: (b) => formatInr(b.unitCost) },
                { key: 'gst', header: 'GST', align: 'end', priority: 'low', render: (b) => formatPercent(b.gstRate) },
                {
                  key: 'state',
                  header: 'Status',
                  render: (b) =>
                    b.isBlocked ? (
                      <Tooltip content={b.blockReason ?? 'On hold'} relationship="description">
                        <Badge appearance="tint" color="danger" icon={<LockClosedRegular />}>
                          On hold
                        </Badge>
                      </Tooltip>
                    ) : b.isExpired ? (
                      <Badge appearance="tint" color="danger">
                        Expired
                      </Badge>
                    ) : b.available > 0 ? (
                      <Badge appearance="tint" color="success">
                        Usable
                      </Badge>
                    ) : (
                      <Badge appearance="tint">{b.onHand > 0 ? 'Reserved' : 'Finished'}</Badge>
                    ),
                },
              ]}
              rowActions={
                canHold || itemCode
                  ? (b) =>
                      b.onHand > 0 ? (
                        <span style={{ display: 'inline-flex', gap: 4 }}>
                          {itemCode && !b.isBlocked && !b.isExpired && (
                            <Button size="small" icon={<TagRegular />} onClick={() => setTagging(b)}>
                              Tags
                            </Button>
                          )}
                          {canHold &&
                            (b.isBlocked ? (
                              <Button size="small" icon={<LockOpenRegular />} onClick={() => setReleasing(b)}>
                                Release
                              </Button>
                            ) : (
                              <Button size="small" icon={<LockClosedRegular />} onClick={() => setBlocking(b)}>
                                Put on hold
                              </Button>
                            ))}
                        </span>
                      ) : null
                  : undefined
              }
              emptyTitle="No batches in this store"
              emptyDescription="Stock arrives through goods receipts, transfers and indent issues."
            />
          )}

          {(reservations.data?.length ?? 0) > 0 && (
            <div>
              <Subtitle2 as="h3">Set aside for documents</Subtitle2>
              <Caption1 block style={{ marginBottom: 8 }}>
                Approved indents reserve stock so nobody else can take it.
              </Caption1>
              <DataTable
                ariaLabel="Reservations"
                rows={reservations.data}
                getRowId={(r) => r.id}
                size="small"
                columns={[
                  {
                    key: 'doc',
                    header: 'Document',
                    render: (r) => `${referenceModuleLabel(r.referenceModule)} ${r.referenceNumber ?? ''}`,
                  },
                  { key: 'batch', header: 'Batch', render: (r) => <Text font="monospace">{r.batchNumber}</Text> },
                  { key: 'qty', header: 'Reserved', align: 'end', render: (r) => formatQty(r.quantity) },
                  { key: 'left', header: 'Still reserved', align: 'end', render: (r) => formatQty(r.outstanding) },
                  { key: 'at', header: 'Since', priority: 'low', render: (r) => formatDateTime(r.createdAt) },
                ]}
              />
            </div>
          )}
        </div>
      </DrawerBody>
      <DrawerFooter>
        {canSeeLedger && (
          <Button
            icon={<HistoryRegular />}
            onClick={() => void navigate({ to: '/inventory/ledger', search: { storeId, itemId, itemName } })}
          >
            Stock card
          </Button>
        )}
        <Button onClick={onClose}>Close</Button>
      </DrawerFooter>

      {tagging && itemCode && (
        <PrintTagsDialog
          title={`Pharmacy tags for batch ${tagging.batchNumber}`}
          tags={[
            {
              itemCode,
              medicine: itemName,
              batchNumber: tagging.batchNumber,
              expiryDate: tagging.expiryDate,
              mrp: tagging.mrp,
              unit: baseUnit ?? null,
              copies: 1,
            },
          ]}
          onClose={() => setTagging(null)}
        />
      )}
      {blocking && (
        <ReasonDialog
          title={`Put batch ${blocking.batchNumber} on hold?`}
          confirmLabel="Put on hold"
          destructive
          placeholder="e.g. Recall notice 14/2026, quality complaint"
          busy={block.isPending}
          error={block.error}
          onSubmit={(reason) => block.mutate({ batch: blocking, reason })}
          onClose={() => {
            block.reset();
            setBlocking(null);
          }}
        >
          Held stock stays on the shelf but is never sold, dispensed, issued or transferred until someone releases it.
        </ReasonDialog>
      )}
      <ConfirmDialog
        open={releasing !== null}
        title={`Release batch ${releasing?.batchNumber ?? ''}?`}
        confirmLabel="Release"
        busy={release.isPending}
        onConfirm={() => releasing && release.mutate(releasing)}
        onCancel={() => setReleasing(null)}
      >
        The batch can be used again straight away
        {releasing?.blockReason ? `. It was held for: ${releasing.blockReason}` : ''}.
      </ConfirmDialog>
    </OverlayDrawer>
  );
}
