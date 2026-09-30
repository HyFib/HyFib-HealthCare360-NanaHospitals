import { Button, makeStyles, Text } from '@fluentui/react-components';
import {
  BoxArrowLeftRegular,
  CheckmarkCircleRegular,
  ClipboardTaskListLtrRegular,
  DocumentBulletListRegular,
  ReceiptAddRegular,
  SendRegular,
  VehicleTruckProfileRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatInr } from '@hc360/core';
import { DataTable, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { DocStatus } from '@/modules/inventory/StockBits';
import { orderStatusLabel, purchaseQueries } from '@/modules/purchase/api';

export const Route = createFileRoute('/_app/purchase/')({
  component: PurchaseOverviewPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' },
});

/** Procurement at a glance: what waits for approval, what to send, what is due to arrive. */
function PurchaseOverviewPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canManage = usePermission(S.purchaseManage);
  const canReceive = usePermission(S.grnReceive);
  const requisitionsWaiting = useQuery(purchaseQueries.count('requisitions', 'Submitted'));
  const requisitionsToOrder = useQuery(purchaseQueries.count('requisitions', 'Approved'));
  const ordersWaiting = useQuery(purchaseQueries.count('orders', 'Submitted'));
  const ordersToSend = useQuery(purchaseQueries.count('orders', 'Approved'));
  const ordersSent = useQuery(purchaseQueries.count('orders', 'Sent'));
  const ordersPartial = useQuery(purchaseQueries.count('orders', 'PartiallyReceived'));
  const grnDrafts = useQuery(purchaseQueries.count('grns', 'Draft'));
  const recent = useQuery(purchaseQueries.orders({}));
  const n = (value: number | undefined) => (value === undefined ? '…' : value.toLocaleString('en-IN'));
  const due =
    ordersSent.data === undefined || ordersPartial.data === undefined
      ? undefined
      : ordersSent.data + ordersPartial.data;

  return (
    <Page>
      <PageHeader
        title="Purchase"
        subtitle="Requisition → purchase order → goods receipt"
        actions={
          <>
            {canManage && (
              <Button icon={<ReceiptAddRegular />} onClick={() => void navigate({ to: '/purchase/orders/new' })}>
                New purchase order
              </Button>
            )}
            {canReceive && (
              <Button
                appearance="primary"
                icon={<BoxArrowLeftRegular />}
                onClick={() => void navigate({ to: '/purchase/grns/new' })}
              >
                Receive goods
              </Button>
            )}
          </>
        }
      />
      <div className={styles.kpis}>
        <KpiCard
          label="Requisitions to approve"
          value={n(requisitionsWaiting.data)}
          icon={<ClipboardTaskListLtrRegular />}
          onClick={() => void navigate({ to: '/purchase/requisitions', search: { status: 'Submitted' } })}
        />
        <KpiCard
          label="Approved, not ordered"
          value={n(requisitionsToOrder.data)}
          icon={<CheckmarkCircleRegular />}
          hint="Turn them into purchase orders"
          onClick={() => void navigate({ to: '/purchase/requisitions', search: { status: 'Approved' } })}
        />
        <KpiCard
          label="Orders to approve"
          value={n(ordersWaiting.data)}
          icon={<DocumentBulletListRegular />}
          onClick={() => void navigate({ to: '/purchase/orders', search: { status: 'Submitted' } })}
        />
        <KpiCard
          label="Orders to send"
          value={n(ordersToSend.data)}
          icon={<SendRegular />}
          onClick={() => void navigate({ to: '/purchase/orders', search: { status: 'Approved' } })}
        />
        <KpiCard
          label="Awaiting delivery"
          value={n(due)}
          icon={<VehicleTruckProfileRegular />}
          hint="Sent or partly received"
          onClick={() => void navigate({ to: '/purchase/orders', search: { status: 'Sent' } })}
        />
        <KpiCard
          label="Receipts not posted"
          value={n(grnDrafts.data)}
          icon={<BoxArrowLeftRegular />}
          hint="Draft GRNs"
          onClick={() => void navigate({ to: '/purchase/grns', search: { status: 'Draft' } })}
        />
      </div>
      <Section title="Latest purchase orders" flush>
        <DataTable
          ariaLabel="Latest purchase orders"
          rows={recent.data?.items.slice(0, 10)}
          loading={recent.isLoading}
          error={recent.error}
          onRetry={() => void recent.refetch()}
          getRowId={(o) => o.id}
          onRowClick={(o) => void navigate({ to: '/purchase/orders/$orderId', params: { orderId: o.id } })}
          size="small"
          columns={[
            { key: 'no', header: 'Order', render: (o) => <Text font="monospace">{o.number}</Text> },
            { key: 'vendor', header: 'Vendor', render: (o) => o.vendorName },
            { key: 'store', header: 'Store', priority: 'low', render: (o) => o.storeName },
            { key: 'date', header: 'Date', render: (o) => formatDateOnly(o.orderDate) },
            { key: 'net', header: 'Value', align: 'end', render: (o) => formatInr(o.netAmount) },
            {
              key: 'status',
              header: 'Status',
              render: (o) => <DocStatus status={o.status} label={orderStatusLabel[o.status]} />,
            },
          ]}
          emptyTitle="No purchase orders yet"
        />
      </Section>
    </Page>
  );
}
