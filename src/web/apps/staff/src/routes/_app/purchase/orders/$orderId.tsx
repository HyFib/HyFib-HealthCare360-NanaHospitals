import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowUndoRegular,
  BoxArrowLeftRegular,
  CheckmarkRegular,
  DismissCircleRegular,
  PrintRegular,
  SendRegular,
  StopRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr } from '@hc360/core';
import { ConfirmDialog, DataTable, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { formatPercent, formatQty } from '@/modules/inventory/format';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { Detail, DetailGrid, DocStatus } from '@/modules/inventory/StockBits';
import { useLetterhead } from '@/modules/pharmacy/counter';
import {
  orderStatusLabel,
  purchaseApi,
  purchaseKeys,
  purchaseQueries,
  type PurchaseOrderDetail,
  type PurchaseOrderLineDto,
} from '@/modules/purchase/api';
import { GstSplitNote, PurchaseTotals } from '@/modules/purchase/GstSplitNote';
import { explainGstSplit, type GstSplit } from '@/modules/purchase/gstPreview';
import { useFacilityGstin } from '@/modules/purchase/hooks';
import { OrderForm } from '@/modules/purchase/OrderForm';
import { printPurchaseOrder } from '@/modules/purchase/print';

export const Route = createFileRoute('/_app/purchase/orders/$orderId')({
  component: OrderPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 340px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { display: 'flex', flexDirection: 'column', gap: '16px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

type Action = 'approve' | 'send' | 'return' | 'close' | 'cancel';

function OrderPage() {
  const { orderId } = Route.useParams();
  const navigate = useNavigate();
  const canManage = usePermission(S.purchaseManage);
  const order = useQuery(purchaseQueries.order(orderId));
  if (order.isLoading) {
    return <LoadingState />;
  }
  if (!order.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={order.error} onRetry={() => void order.refetch()} />
      </Page>
    );
  }
  const o = order.data;
  if (o.status === 'Draft' && canManage) {
    return (
      <Page>
        <PageHeader
          title={`Purchase order ${o.number}`}
          subtitle="Draft: change it freely, then send it for approval"
          breadcrumbs={[
            { label: 'Purchase orders', onClick: () => void navigate({ to: '/purchase/orders' }) },
            { label: o.number },
          ]}
        />
        {o.reviewNote && (
          <MessageBar intent="warning">
            <MessageBarBody>
              <MessageBarTitle>Returned for correction</MessageBarTitle>
              {o.reviewNote}
            </MessageBarBody>
          </MessageBar>
        )}
        <OrderForm
          key={o.version}
          order={o}
          onSaved={() => void order.refetch()}
          onCancel={() => void navigate({ to: '/purchase/orders' })}
        />
      </Page>
    );
  }
  return <OrderView order={o} />;
}

function OrderView({ order: o }: { order: PurchaseOrderDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const facilityGstin = useFacilityGstin();
  const canManage = usePermission(S.purchaseManage);
  const canApprove = usePermission(S.purchaseApprove);
  const canReceive = usePermission(S.grnReceive);
  const [action, setAction] = useState<Action | null>(null);
  const split = explainGstSplit(o.vendorGstin, facilityGstin ?? null);
  // The saved order decides the split; the explanation names the states when both GSTINs are known.
  const savedSplit: GstSplit = o.isInterState
    ? {
        interState: true,
        known: true,
        headline: 'IGST (inter-state supply)',
        detail: split.interState
          ? split.detail
          : 'The vendor is registered in another state, so the full GST is charged as IGST.',
      }
    : {
        interState: false,
        known: true,
        headline: 'CGST + SGST (same state)',
        detail: split.interState
          ? 'Saved as a supply within the state: GST is split equally into CGST and SGST.'
          : split.detail,
      };
  const receivable = o.status === 'Approved' || o.status === 'Sent' || o.status === 'PartiallyReceived';
  const cancellable =
    o.status === 'Draft' || o.status === 'Submitted' || o.status === 'Approved' || o.status === 'Sent';

  const run = useMutation({
    mutationFn: ({ kind, reason }: { kind: Action; reason?: string }) => {
      switch (kind) {
        case 'approve':
          return purchaseApi.approveOrder(o.id);
        case 'send':
          return purchaseApi.sendOrder(o.id);
        case 'return':
          return purchaseApi.returnOrder(o.id, reason ?? '');
        case 'close':
          return purchaseApi.closeOrder(o.id, reason ?? '');
        case 'cancel':
          return purchaseApi.cancelOrder(o.id, reason ?? '');
      }
    },
    onSuccess: (_, { kind }) => {
      setAction(null);
      const messages: Record<Action, string> = {
        approve: 'Order approved',
        send: 'Marked as sent to the vendor',
        return: 'Order returned for correction',
        close: 'Order closed',
        cancel: 'Order cancelled',
      };
      notify.success(messages[kind]);
      void queryClient.invalidateQueries({ queryKey: purchaseKeys.all });
      if (kind === 'send') {
        void printPurchaseOrder(o, letterhead).catch((error: unknown) => notify.error(error, 'Printing failed'));
      }
    },
  });

  const onConfirm = () => action && run.mutate({ kind: action });

  return (
    <Page>
      <PageHeader
        title={`Purchase order ${o.number}`}
        subtitle={`${o.vendorName} → ${o.storeName}`}
        breadcrumbs={[
          { label: 'Purchase orders', onClick: () => void navigate({ to: '/purchase/orders' }) },
          { label: o.number },
        ]}
        actions={
          <>
            <Button
              icon={<PrintRegular />}
              onClick={() =>
                void printPurchaseOrder(o, letterhead).catch((error: unknown) => notify.error(error, 'Printing failed'))
              }
            >
              Print
            </Button>
            {cancellable && (canManage || canApprove) && (
              <Button icon={<DismissCircleRegular />} onClick={() => setAction('cancel')}>
                Cancel order
              </Button>
            )}
            {canManage && (o.status === 'Sent' || o.status === 'PartiallyReceived' || o.status === 'Approved') && (
              <Button icon={<StopRegular />} onClick={() => setAction('close')}>
                Short-close
              </Button>
            )}
            {canApprove && o.status === 'Submitted' && (
              <>
                <Button icon={<ArrowUndoRegular />} onClick={() => setAction('return')}>
                  Return for correction
                </Button>
                <Button appearance="primary" icon={<CheckmarkRegular />} onClick={() => setAction('approve')}>
                  Approve
                </Button>
              </>
            )}
            {canManage && o.status === 'Approved' && (
              <Button
                appearance={canReceive ? 'secondary' : 'primary'}
                icon={<SendRegular />}
                onClick={() => setAction('send')}
              >
                Send to vendor
              </Button>
            )}
            {canReceive && receivable && (
              <Button
                appearance="primary"
                icon={<BoxArrowLeftRegular />}
                onClick={() => void navigate({ to: '/purchase/grns/new', search: { orderId: o.id } })}
              >
                Receive goods
              </Button>
            )}
          </>
        }
      />
      {o.status === 'Submitted' && !canApprove && (
        <MessageBar intent="info">
          <MessageBarBody>Waiting for approval by a purchase approver.</MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.layout}>
        <div className={styles.main}>
          <Section>
            <DetailGrid>
              <Detail label="Status">
                <DocStatus status={o.status} label={orderStatusLabel[o.status]} />
              </Detail>
              <Detail label="Vendor">
                {o.vendorName}
                {o.vendorGstin ? ` · ${o.vendorGstin}` : ''}
              </Detail>
              <Detail label="Deliver to">{o.storeName}</Detail>
              <Detail label="Order date">{formatDateOnly(o.orderDate)}</Detail>
              <Detail label="Expected">{o.expectedDeliveryDate ? formatDateOnly(o.expectedDeliveryDate) : null}</Detail>
              <Detail label="Credit">{o.paymentTermsDays ? `${o.paymentTermsDays} days` : 'On delivery'}</Detail>
              {o.approvedAt && <Detail label="Approved">{formatDateTime(o.approvedAt)}</Detail>}
              {o.sentAt && <Detail label="Sent">{formatDateTime(o.sentAt)}</Detail>}
              {o.closedAt && (
                <Detail label={o.status === 'Cancelled' ? 'Cancelled' : 'Closed'}>{formatDateTime(o.closedAt)}</Detail>
              )}
              {o.closeReason && <Detail label="Reason">{o.closeReason}</Detail>}
              {o.remarks && <Detail label="Remarks">{o.remarks}</Detail>}
            </DetailGrid>
          </Section>
          <Section title="Items" flush>
            <DataTable<PurchaseOrderLineDto>
              ariaLabel="Order lines"
              rows={o.lines}
              getRowId={(l) => l.id}
              size="small"
              columns={[
                {
                  key: 'item',
                  header: 'Item',
                  render: (l) => (
                    <span>
                      <Text weight="semibold">{l.itemName}</Text>
                      <Caption1 block className={styles.muted}>
                        {formatQty(l.quantity, l.purchaseUnit)}
                        {l.freeQuantity ? ` + ${formatQty(l.freeQuantity)} free` : ''} @ {formatInr(l.rate)}
                        {l.discountPercent ? ` less ${formatPercent(l.discountPercent)}` : ''}
                      </Caption1>
                    </span>
                  ),
                },
                { key: 'gst', header: 'GST', align: 'end', width: 70, render: (l) => formatPercent(l.gstRate) },
                {
                  key: 'taxable',
                  header: 'Taxable',
                  align: 'end',
                  priority: 'low',
                  render: (l) => formatInr(l.taxableAmount),
                },
                {
                  key: 'tax',
                  header: o.isInterState ? 'IGST' : 'CGST + SGST',
                  align: 'end',
                  priority: 'low',
                  render: (l) =>
                    o.isInterState
                      ? formatInr(l.igstAmount)
                      : `${formatInr(l.cgstAmount)} + ${formatInr(l.sgstAmount)}`,
                },
                {
                  key: 'total',
                  header: 'Amount',
                  align: 'end',
                  render: (l) => <Text weight="semibold">{formatInr(l.lineTotal)}</Text>,
                },
                {
                  key: 'received',
                  header: 'Received',
                  align: 'end',
                  render: (l) => (
                    <span>
                      {formatQty(l.receivedQuantity)}
                      {l.pendingQuantity > 0 && receivable && (
                        <Caption1 block>{formatQty(l.pendingQuantity)} pending</Caption1>
                      )}
                    </span>
                  ),
                },
              ]}
            />
          </Section>
        </div>
        <aside className={styles.aside}>
          <GstSplitNote split={savedSplit} />
          <Section title="Order value">
            <PurchaseTotals
              totals={{
                gross: o.grossAmount,
                discount: o.discountAmount,
                taxable: o.taxableAmount,
                cgst: o.cgstAmount,
                sgst: o.sgstAmount,
                igst: o.igstAmount,
                roundOff: o.roundOff,
                net: o.netAmount,
              }}
              interState={o.isInterState}
            />
          </Section>
        </aside>
      </div>

      <ConfirmDialog
        open={action === 'approve' || action === 'send'}
        title={action === 'approve' ? `Approve order ${o.number}?` : `Mark order ${o.number} as sent?`}
        confirmLabel={action === 'approve' ? 'Approve' : 'Mark as sent and print'}
        busy={run.isPending}
        onConfirm={onConfirm}
        onCancel={() => setAction(null)}
      >
        {action === 'approve'
          ? `The order for ${formatInr(o.netAmount)} can then be sent to ${o.vendorName}.`
          : `Send the printed or e-mailed order to ${o.vendorName}. Goods can be received against it from now on.`}
      </ConfirmDialog>
      {(action === 'return' || action === 'close' || action === 'cancel') && (
        <ReasonDialog
          title={
            action === 'return'
              ? 'Return the order for correction?'
              : action === 'close'
                ? `Short-close order ${o.number}?`
                : `Cancel order ${o.number}?`
          }
          confirmLabel={action === 'return' ? 'Return' : action === 'close' ? 'Short-close' : 'Cancel order'}
          destructive={action !== 'return'}
          busy={run.isPending}
          error={run.error}
          onSubmit={(reason) => run.mutate({ kind: action, reason })}
          onClose={() => {
            run.reset();
            setAction(null);
          }}
        >
          {action === 'return'
            ? 'The order goes back to draft with your note so it can be corrected and sent again.'
            : action === 'close'
              ? 'The vendor will not supply the balance. What was received stays; nothing more can be received on this order.'
              : 'Orders with receipts cannot be cancelled; short-close them instead.'}
        </ReasonDialog>
      )}
    </Page>
  );
}
