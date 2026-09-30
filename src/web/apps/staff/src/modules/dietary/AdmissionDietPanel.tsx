import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, EditRegular, FoodRegular, StopRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { DIET_SOURCE_LABEL, dietaryApi, dietaryKeys, dietaryQueries, type DietOrderDto } from './api';
import { DietOrderDialog, type DietAdmission } from './DietOrderDialog';
import { describeSlots, PREFERENCE_LABEL } from './rules';

const useStyles = makeStyles({
  current: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    marginBottom: '16px',
  },
  head: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap', justifyContent: 'space-between' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  allergy: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The admission's diet: the current order with its rounds and restrictions, changes and history (DIET-01). */
export function AdmissionDietPanel({ admission, canOrder }: { admission: DietAdmission; canOrder: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('dietary.orders.manage') && canOrder;
  const orders = useQuery(dietaryQueries.admissionOrders(admission.admissionId));
  const [editing, setEditing] = useState<'new' | DietOrderDto | null>(null);
  const [stopping, setStopping] = useState<DietOrderDto | null>(null);
  const current = orders.data?.find((o) => o.status === 'Active') ?? null;

  const refresh = () => void queryClient.invalidateQueries({ queryKey: dietaryKeys.all });
  const review = useMutation({
    mutationFn: (order: DietOrderDto) => dietaryApi.reviewOrder(order.id),
    onSuccess: () => {
      notify.success('Diet reviewed');
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not mark the diet as reviewed'),
  });
  const stop = useMutation({
    mutationFn: ({ order, reason }: { order: DietOrderDto; reason: string }) => dietaryApi.stopOrder(order.id, reason),
    onSuccess: () => {
      notify.success('Diet stopped', 'Meals not yet served were cancelled.');
      setStopping(null);
      refresh();
    },
  });

  if (orders.error) {
    return <ErrorState error={orders.error} onRetry={() => void orders.refetch()} />;
  }

  return (
    <div>
      {current ? (
        <section className={styles.current} aria-label="Current diet">
          <div className={styles.head}>
            <Subtitle2 as="h3">
              {current.dietName ?? current.dietCode} · {PREFERENCE_LABEL[current.preference]}
            </Subtitle2>
            {canManage && (
              <div className={styles.actions}>
                {current.needsReview && (
                  <Button
                    size="small"
                    appearance="primary"
                    icon={<CheckmarkRegular />}
                    disabled={review.isPending}
                    onClick={() => review.mutate(current)}
                  >
                    Mark reviewed
                  </Button>
                )}
                <Button size="small" icon={<EditRegular />} onClick={() => setEditing(current)}>
                  Change diet
                </Button>
                <Button size="small" icon={<StopRegular />} onClick={() => setStopping(current)}>
                  Stop
                </Button>
              </div>
            )}
          </div>
          {current.needsReview && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <MessageBarTitle>Needs a dietitian's review</MessageBarTitle>
                Created from a clinical order with a default diet. Confirm or change it.
              </MessageBarBody>
            </MessageBar>
          )}
          <Text>{describeSlots(current.meals)}</Text>
          {current.restrictions.length > 0 && <Text>Restrictions: {current.restrictions.join(', ')}</Text>}
          {current.allergyNotes && (
            <Text className={styles.allergy}>
              <WarningRegular aria-hidden /> Food allergies: {current.allergyNotes}
            </Text>
          )}
          {(current.fluidRestrictionMl || current.calorieTargetKcal) && (
            <Text>
              {current.fluidRestrictionMl ? `Fluids up to ${current.fluidRestrictionMl} ml/day` : ''}
              {current.fluidRestrictionMl && current.calorieTargetKcal ? ' · ' : ''}
              {current.calorieTargetKcal ? `${current.calorieTargetKcal} kcal/day` : ''}
            </Text>
          )}
          {current.instructions && <Text>Kitchen: {current.instructions}</Text>}
          <Caption1 className={styles.muted}>
            From {formatDateOnly(current.startDate)} · ordered by{' '}
            {current.orderedByName ?? DIET_SOURCE_LABEL[current.source]} ({DIET_SOURCE_LABEL[current.source]})
          </Caption1>
        </section>
      ) : orders.data && orders.data.length > 0 ? (
        <MessageBar intent="info" style={{ marginBottom: 12 }}>
          <MessageBarBody>No diet is active now, so no meals are planned.</MessageBarBody>
          {canManage && (
            <MessageBarActions>
              <Button size="small" onClick={() => setEditing('new')}>
                Order diet
              </Button>
            </MessageBarActions>
          )}
        </MessageBar>
      ) : orders.data ? (
        <EmptyState
          icon={<FoodRegular />}
          title="No diet ordered"
          description="The patient gets no hospital meals until a diet is ordered."
          action={
            canManage ? (
              <Button appearance="primary" icon={<FoodRegular />} onClick={() => setEditing('new')}>
                Order diet
              </Button>
            ) : undefined
          }
        />
      ) : null}

      {orders.data && orders.data.length > 0 && (
        <DataTable
          ariaLabel="Diet history"
          rows={orders.data}
          getRowId={(o) => o.id}
          size="small"
          columns={[
            {
              key: 'diet',
              header: 'Diet',
              render: (o) => `${o.dietName ?? o.dietCode ?? ''} · ${PREFERENCE_LABEL[o.preference]}`,
            },
            { key: 'from', header: 'From', width: 130, render: (o) => formatDateOnly(o.startDate) },
            { key: 'to', header: 'Until', width: 130, render: (o) => (o.endDate ? formatDateOnly(o.endDate) : '—') },
            {
              key: 'by',
              header: 'Ordered',
              priority: 'low',
              render: (o) => `${o.orderedByName ?? ''} · ${formatDateTime(o.createdAt)}`,
            },
            {
              key: 'status',
              header: 'Status',
              width: 150,
              render: (o) => (
                <span>
                  <StatusBadge status={o.status} tone={o.status === 'Active' ? 'success' : 'informative'} />
                  {o.needsReview && (
                    <Badge size="small" appearance="tint" color="warning" style={{ marginLeft: 4 }}>
                      Review
                    </Badge>
                  )}
                  {o.stopReason && <Caption1 block>{o.stopReason}</Caption1>}
                </span>
              ),
            },
          ]}
          emptyTitle="No diet orders"
        />
      )}
      {orders.isLoading && <Caption1 className={styles.muted}>Loading diet orders…</Caption1>}

      {editing && (
        <DietOrderDialog
          admission={admission}
          current={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
      <ReasonDialog
        open={stopping !== null}
        title="Stop this diet?"
        description="Meals not yet served are cancelled. Order a new diet when the patient can eat again."
        placeholder="e.g. Nil by mouth for surgery tomorrow; discharged"
        confirmLabel="Stop diet"
        destructive
        busy={stop.isPending}
        error={stop.error}
        onConfirm={(reason) => stopping && stop.mutate({ order: stopping, reason })}
        onCancel={() => {
          setStopping(null);
          stop.reset();
        }}
      />
    </div>
  );
}
