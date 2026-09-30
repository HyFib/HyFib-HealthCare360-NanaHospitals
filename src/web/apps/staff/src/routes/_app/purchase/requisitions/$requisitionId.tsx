import { Button, Text } from '@fluentui/react-components';
import {
  CheckmarkRegular,
  DismissCircleRegular,
  OpenRegular,
  ReceiptAddRegular,
  StopRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { ConfirmDialog, DataTable, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { formatQty } from '@/modules/inventory/format';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { Detail, DetailGrid, DocStatus } from '@/modules/inventory/StockBits';
import { useItemMaster } from '@/modules/pharmacy/counter';
import { purchaseApi, purchaseKeys, purchaseQueries } from '@/modules/purchase/api';

export const Route = createFileRoute('/_app/purchase/requisitions/$requisitionId')({
  component: RequisitionPage,
});

function RequisitionPage() {
  const { requisitionId } = Route.useParams();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission(S.purchaseManage);
  const canApprove = usePermission(S.purchaseApprove);
  const requisition = useQuery(purchaseQueries.requisition(requisitionId));
  const master = useItemMaster(requisition.data?.lines.map((l) => l.itemId) ?? []);
  const [dialog, setDialog] = useState<'approve' | 'reject' | 'cancel' | null>(null);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: purchaseKeys.all });
  const approve = useMutation({
    mutationFn: () => purchaseApi.approveRequisition(requisitionId),
    onSuccess: () => {
      setDialog(null);
      notify.success('Requisition approved', 'Purchase can now raise the order.');
      refresh();
    },
    onError: (error) => {
      setDialog(null);
      notify.error(error, 'Could not approve');
    },
  });
  const decide = useMutation({
    mutationFn: ({ kind, reason }: { kind: 'reject' | 'cancel'; reason: string }) =>
      kind === 'reject'
        ? purchaseApi.rejectRequisition(requisitionId, reason)
        : purchaseApi.cancelRequisition(requisitionId, reason),
    onSuccess: (_, { kind }) => {
      setDialog(null);
      notify.success(kind === 'reject' ? 'Requisition rejected' : 'Requisition cancelled');
      refresh();
    },
  });

  if (requisition.isLoading) {
    return <LoadingState />;
  }
  if (!requisition.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={requisition.error} onRetry={() => void requisition.refetch()} />
      </Page>
    );
  }
  const r = requisition.data;

  return (
    <Page maxWidth={1200}>
      <PageHeader
        title={`Requisition ${r.number}`}
        subtitle={`For ${r.storeName}`}
        breadcrumbs={[
          { label: 'Requisitions', onClick: () => void navigate({ to: '/purchase/requisitions' }) },
          { label: r.number },
        ]}
        actions={
          <>
            {canManage && (r.status === 'Submitted' || r.status === 'Approved') && (
              <Button icon={<DismissCircleRegular />} onClick={() => setDialog('cancel')}>
                Cancel
              </Button>
            )}
            {canApprove && r.status === 'Submitted' && (
              <>
                <Button icon={<StopRegular />} onClick={() => setDialog('reject')}>
                  Reject
                </Button>
                <Button appearance="primary" icon={<CheckmarkRegular />} onClick={() => setDialog('approve')}>
                  Approve
                </Button>
              </>
            )}
            {canManage && r.status === 'Approved' && (
              <Button
                appearance="primary"
                icon={<ReceiptAddRegular />}
                onClick={() => void navigate({ to: '/purchase/orders/new', search: { requisitionId: r.id } })}
              >
                Create purchase order
              </Button>
            )}
            {r.purchaseOrderId && (
              <Button
                icon={<OpenRegular />}
                onClick={() =>
                  void navigate({ to: '/purchase/orders/$orderId', params: { orderId: r.purchaseOrderId! } })
                }
              >
                Open the order
              </Button>
            )}
          </>
        }
      />
      <Section>
        <DetailGrid>
          <Detail label="Status">
            <DocStatus status={r.status} />
          </Detail>
          <Detail label="Needed by">{r.requiredBy ? formatDateOnly(r.requiredBy) : null}</Detail>
          <Detail label="Raised">{formatDateTime(r.createdAt)}</Detail>
          {r.decidedAt && <Detail label="Decided">{formatDateTime(r.decidedAt)}</Detail>}
          {r.remarks && <Detail label="Remarks">{r.remarks}</Detail>}
          {r.rejectReason && <Detail label="Reason">{r.rejectReason}</Detail>}
        </DetailGrid>
      </Section>
      <Section title="Items" flush>
        <DataTable
          ariaLabel="Requisition lines"
          rows={r.lines}
          getRowId={(l) => l.id}
          size="small"
          columns={[
            { key: 'item', header: 'Item', render: (l) => <Text weight="semibold">{l.itemName}</Text> },
            {
              key: 'qty',
              header: 'Quantity',
              align: 'end',
              render: (l) => formatQty(l.quantity, master.items.get(l.itemId)?.purchaseUnit),
            },
            { key: 'remarks', header: 'Note', render: (l) => l.remarks ?? '—' },
          ]}
        />
      </Section>
      <ConfirmDialog
        open={dialog === 'approve'}
        title={`Approve requisition ${r.number}?`}
        confirmLabel="Approve"
        busy={approve.isPending}
        onConfirm={() => approve.mutate()}
        onCancel={() => setDialog(null)}
      >
        Purchase can then raise an order for these {r.lines.length} items.
      </ConfirmDialog>
      {(dialog === 'reject' || dialog === 'cancel') && (
        <ReasonDialog
          title={dialog === 'reject' ? `Reject requisition ${r.number}?` : `Cancel requisition ${r.number}?`}
          confirmLabel={dialog === 'reject' ? 'Reject' : 'Cancel requisition'}
          destructive
          busy={decide.isPending}
          error={decide.error}
          onSubmit={(reason) => decide.mutate({ kind: dialog, reason })}
          onClose={() => {
            decide.reset();
            setDialog(null);
          }}
        >
          The store that raised it sees your reason.
        </ReasonDialog>
      )}
    </Page>
  );
}
