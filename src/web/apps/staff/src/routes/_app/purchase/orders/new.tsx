import { EmptyState, ErrorState, LoadingState, Page, PageHeader } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { purchaseQueries } from '@/modules/purchase/api';
import { OrderForm } from '@/modules/purchase/OrderForm';

export const Route = createFileRoute('/_app/purchase/orders/new')({
  validateSearch: (s: Record<string, unknown>): { requisitionId?: string } => ({
    requisitionId: typeof s.requisitionId === 'string' && s.requisitionId ? s.requisitionId : undefined,
  }),
  component: NewOrderPage,
});

/** New purchase order, blank or from an approved requisition. */
function NewOrderPage() {
  const { requisitionId } = Route.useSearch();
  const navigate = useNavigate();
  const canManage = usePermission(S.purchaseManage);
  const requisition = useQuery({
    ...purchaseQueries.requisition(requisitionId ?? ''),
    enabled: Boolean(requisitionId),
  });

  if (!canManage) {
    return (
      <Page maxWidth={800}>
        <EmptyState
          title="You cannot raise purchase orders"
          description="Ask the purchase team or an administrator for access."
        />
      </Page>
    );
  }
  if (requisitionId && requisition.isLoading) {
    return <LoadingState />;
  }
  if (requisitionId && requisition.error) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={requisition.error} onRetry={() => void requisition.refetch()} />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="New purchase order"
        subtitle={
          requisition.data
            ? `From requisition ${requisition.data.number} for ${requisition.data.storeName}`
            : 'Choose the vendor, the store and the items'
        }
        breadcrumbs={[
          { label: 'Purchase orders', onClick: () => void navigate({ to: '/purchase/orders' }) },
          { label: 'New' },
        ]}
      />
      <OrderForm
        order={null}
        requisition={requisition.data ?? null}
        onSaved={(id) => void navigate({ to: '/purchase/orders/$orderId', params: { orderId: id }, replace: true })}
        onCancel={() => void navigate({ to: requisitionId ? '/purchase/requisitions' : '/purchase/orders' })}
      />
    </Page>
  );
}
