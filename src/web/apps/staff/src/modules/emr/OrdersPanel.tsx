import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Select,
  SpinButton,
  Spinner,
  Tab,
  TabList,
  Text,
  Textarea,
  tokens,
  Tooltip,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  BoxMultipleRegular,
  CheckmarkRegular,
  DeleteRegular,
  DismissRegular,
  MoreHorizontalRegular,
  SendRegular,
  StarRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatTime, isApiError, humanize } from '@hc360/core';
import { EmptyState, LoadingState, Pagination, Section, StatusBadge, useLeaveGuard, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { emrApi, emrKeys, emrQueries, type TemplateServiceDto } from './api';
import { useLiveOrders } from './hooks';
import {
  addManyToCart,
  addToCart,
  maxQuantity,
  ORDER_GROUPS,
  quantityUnit,
  removeFromCart,
  serviceCategoryLabel,
  setNotes,
  setQuantity,
  toOrderLines,
  type CartLine,
  type OrderGroupId,
} from './orderCart';
import {
  ORDER_PRIORITIES,
  orderPriorityLabel,
  orderQueries,
  ordersApi,
  type ClinicalOrderStatus,
  type DuplicateOrderLine,
  type OrderDto,
  type OrderItemDto,
  type OrderPriority,
  type ServiceItemDto,
} from './ordersApi';
import { ServicePicker } from './ServicePicker';
import { ReasonDialog } from './ui';

const useStyles = makeStyles({
  composer: { display: 'flex', flexDirection: 'column', gap: '12px' },
  pickRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(260px, 1fr) auto',
    gap: '8px',
    alignItems: 'end',
    '@media (max-width: 700px)': { gridTemplateColumns: '1fr' },
  },
  chips: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  cart: { display: 'flex', flexDirection: 'column', gap: '6px', margin: 0, padding: 0, listStyle: 'none' },
  cartLine: {
    display: 'grid',
    gridTemplateColumns: 'minmax(180px, 1fr) minmax(140px, 220px) auto auto',
    gap: '8px',
    alignItems: 'center',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 800px)': { gridTemplateColumns: '1fr auto' },
  },
  lineName: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  footer: {
    display: 'grid',
    gridTemplateColumns: 'minmax(160px, 220px) 1fr auto',
    gap: '8px',
    alignItems: 'end',
    '@media (max-width: 800px)': { gridTemplateColumns: '1fr' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  orders: { display: 'flex', flexDirection: 'column', gap: '10px' },
  order: {
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  orderHeader: { display: 'flex', gap: '8px', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' },
  orderTitle: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  item: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', justifyContent: 'space-between' },
  itemName: { display: 'flex', gap: '6px', alignItems: 'baseline', flexWrap: 'wrap' },
  cancelled: { textDecoration: 'line-through', color: tokens.colorNeutralForeground3 },
});

function lineTone(status: OrderItemDto['status']): 'warning' | 'brand' | 'success' | 'danger' {
  return status === 'Ordered'
    ? 'warning'
    : status === 'InProgress'
      ? 'brand'
      : status === 'Completed'
        ? 'success'
        : 'danger';
}

/**
 * Lab, imaging and procedure ordering: search the catalogue, one click for favourites and order sets, priority and a
 * note for the performing department. Services still pending for the visit are flagged before ordering them again.
 */
export function OrderComposer({
  patientId,
  encounterId,
  visitId = null,
  admissionId = null,
  templateOrders = [],
}: {
  patientId: string;
  encounterId: string | null;
  visitId?: string | null;
  admissionId?: string | null;
  templateOrders?: readonly TemplateServiceDto[];
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManageFavourites = usePermission(['clinical.emr.write', 'clinical.prescriptions.write']);
  const [group, setGroup] = useState<OrderGroupId>('all');
  const [cart, setCart] = useState<CartLine[]>([]);
  const [priority, setPriority] = useState<OrderPriority>('Routine');
  const [clinicalNotes, setClinicalNotes] = useState('');
  const [duplicates, setDuplicates] = useState<DuplicateOrderLine[] | null>(null);
  const favourites = useQuery(emrQueries.favourites('Order'));
  const orderSets = useQuery(orderQueries.orderSets());
  const groupInfo = ORDER_GROUPS.find((g) => g.id === group) ?? ORDER_GROUPS[0];

  const addService = (service: { id: string; code: string; name: string; category: ServiceItemDto['category'] }) => {
    setCart((current) => addToCart(current, service));
    setDuplicates(null);
  };

  const addFavourite = useMutation({
    mutationFn: (serviceItemId: string) => queryClient.fetchQuery(orderQueries.service(serviceItemId)),
    onSuccess: (service) => addService(service),
    onError: (error) => notify.error(error, 'Could not add the favourite'),
  });

  const saveFavourite = useMutation({
    mutationFn: (line: CartLine) =>
      emrApi.addFavourite({
        kind: 'Order',
        label: line.name,
        serviceItemId: line.serviceItemId,
        drugItemId: null,
        genericName: null,
        strength: null,
        dosageForm: null,
        route: null,
        dose: null,
        frequency: null,
        customFrequency: null,
        durationDays: null,
        instructions: null,
        isControlledDrug: false,
        displayOrder: (favourites.data?.length ?? 0) + 1,
      }),
    onSuccess: (favourite) => {
      notify.success('Added to favourites', favourite.label);
      void queryClient.invalidateQueries({ queryKey: emrKeys.favourites('Order') });
    },
    onError: (error) => notify.error(error, 'Could not save the favourite'),
  });

  const removeFavourite = useMutation({
    mutationFn: (id: string) => emrApi.removeFavourite(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: emrKeys.favourites('Order') }),
    onError: (error) => notify.error(error, 'Could not remove the favourite'),
  });

  const place = useMutation({
    mutationFn: (confirmDuplicates: boolean) =>
      ordersApi.place({
        patientId,
        encounterId,
        visitId,
        admissionId,
        priority,
        clinicalNotes: clinicalNotes.trim() || null,
        items: toOrderLines(cart),
        orderSetId: null,
        confirmDuplicates,
      }),
    onSuccess: (order) => {
      setCart([]);
      setClinicalNotes('');
      setPriority('Routine');
      setDuplicates(null);
      notify.success(
        `Order ${order.orderNumber} placed`,
        `${order.items.length} ${order.items.length === 1 ? 'service' : 'services'} sent to the departments.`,
      );
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'order.duplicate' && Array.isArray(error.data)) {
        setDuplicates(error.data as DuplicateOrderLine[]);
      } else {
        notify.error(error, 'Could not place the order');
      }
    },
  });

  const favouriteIds = new Set((favourites.data ?? []).map((f) => f.serviceItemId));

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: cart.length > 0 || clinicalNotes !== '', busy: place.isPending });

  return (
    <div className={styles.composer}>
      <TabList
        size="small"
        selectedValue={group}
        onTabSelect={(_, d) => setGroup(d.value as OrderGroupId)}
        aria-label="Service groups"
      >
        {ORDER_GROUPS.map((g) => (
          <Tab key={g.id} value={g.id}>
            {g.label}
          </Tab>
        ))}
      </TabList>
      <div className={styles.pickRow}>
        <ServicePicker
          label="Add test or procedure"
          categories={
            groupInfo.categories ?? [
              'Laboratory',
              'Radiology',
              'Cardiology',
              'Procedure',
              'Surgery',
              'Physiotherapy',
              'Dialysis',
              'Dietary',
              'BloodBank',
              'Consultation',
            ]
          }
          onPick={addService}
          hint="Type at least 2 letters; Enter adds the highlighted service."
        />
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Button icon={<BoxMultipleRegular />} disabled={!orderSets.data || orderSets.data.length === 0}>
              Order sets
            </Button>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              {(orderSets.data ?? []).map((set) => (
                <MenuItem
                  key={set.id}
                  onClick={() =>
                    setCart((current) =>
                      addManyToCart(
                        current,
                        set.services.map((s) => ({
                          id: s.serviceItemId,
                          code: s.code,
                          name: s.name,
                          category: s.category,
                        })),
                      ),
                    )
                  }
                >
                  {set.name} ({set.services.length})
                </MenuItem>
              ))}
            </MenuList>
          </MenuPopover>
        </Menu>
      </div>

      {((favourites.data?.length ?? 0) > 0 || templateOrders.length > 0) && (
        <div className={styles.chips}>
          {templateOrders.length > 0 && (
            <Button
              size="small"
              appearance="primary"
              icon={<AddRegular />}
              onClick={() =>
                setCart((current) =>
                  addManyToCart(
                    current,
                    templateOrders.map((s) => ({
                      id: s.serviceItemId,
                      code: s.code,
                      name: s.name,
                      category: s.category,
                    })),
                  ),
                )
              }
            >
              Template investigations ({templateOrders.length})
            </Button>
          )}
          {(favourites.data ?? []).map((favourite) => (
            <span key={favourite.id} style={{ display: 'inline-flex' }}>
              <Button
                size="small"
                icon={<StarRegular />}
                disabled={!favourite.serviceItemId || addFavourite.isPending}
                onClick={() => favourite.serviceItemId && addFavourite.mutate(favourite.serviceItemId)}
              >
                {favourite.label}
              </Button>
              {canManageFavourites && (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DismissRegular />}
                  aria-label={`Remove ${favourite.label} from favourites`}
                  onClick={() => removeFavourite.mutate(favourite.id)}
                />
              )}
            </span>
          ))}
        </div>
      )}

      {cart.length === 0 ? (
        <Caption1 className={styles.muted}>No services added yet.</Caption1>
      ) : (
        <ul className={styles.cart} aria-label="Services to order">
          {cart.map((line) => {
            const max = maxQuantity(line.category);
            return (
              <li key={line.serviceItemId} className={styles.cartLine}>
                <span className={styles.lineName}>
                  <Text weight="semibold">{line.name}</Text>
                  <Caption1 className={styles.muted}>
                    {serviceCategoryLabel[line.category]} · {line.code}
                  </Caption1>
                </span>
                <Input
                  size="small"
                  placeholder="Note for the department"
                  value={line.notes}
                  onChange={(_, d) => setCart((current) => setNotes(current, line.serviceItemId, d.value))}
                  aria-label={`Note for ${line.name}`}
                />
                {max > 1 ? (
                  <Field size="small" label={quantityUnit(line.category)} orientation="horizontal">
                    <SpinButton
                      size="small"
                      min={1}
                      max={max}
                      value={line.quantity}
                      onChange={(_, d) => {
                        const next = d.value ?? Number(d.displayValue);
                        setCart((current) => setQuantity(current, line.serviceItemId, next));
                      }}
                      style={{ width: 80 }}
                    />
                  </Field>
                ) : (
                  <span />
                )}
                <span>
                  {canManageFavourites && !favouriteIds.has(line.serviceItemId) && (
                    <Tooltip content="Save as a favourite" relationship="label">
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<StarRegular />}
                        onClick={() => saveFavourite.mutate(line)}
                      />
                    </Tooltip>
                  )}
                  <Tooltip content="Remove" relationship="label">
                    <Button
                      size="small"
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      onClick={() => setCart((current) => removeFromCart(current, line.serviceItemId))}
                    />
                  </Tooltip>
                </span>
              </li>
            );
          })}
        </ul>
      )}

      {duplicates && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Already ordered and still pending</MessageBarTitle>
            {duplicates.map((d) => `${d.serviceName} (order ${d.orderNumber}, ${formatTime(d.orderedAt)})`).join('; ')}.
            Order them again only if a repeat test is really needed.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={() => place.mutate(true)} disabled={place.isPending}>
              Order again anyway
            </Button>
            <Button appearance="subtle" onClick={() => setDuplicates(null)}>
              Go back
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {cart.length > 0 && (
        <div className={styles.footer}>
          <Field label="Priority">
            <Select value={priority} onChange={(_, d) => setPriority(d.value as OrderPriority)}>
              {ORDER_PRIORITIES.map((p) => (
                <option key={p} value={p}>
                  {orderPriorityLabel[p]}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Clinical notes for the departments">
            <Textarea
              value={clinicalNotes}
              onChange={(_, d) => setClinicalNotes(d.value.slice(0, 1000))}
              rows={1}
              resize="vertical"
              placeholder="e.g. ? dengue, fever 3 days"
            />
          </Field>
          <Button
            appearance="primary"
            icon={place.isPending ? <Spinner size="tiny" /> : <SendRegular />}
            disabled={place.isPending || duplicates !== null}
            onClick={() => place.mutate(false)}
          >
            Place order ({cart.length})
          </Button>
        </div>
      )}
    </div>
  );
}

type PendingAction =
  | { kind: 'cancel-order'; order: OrderDto }
  | { kind: 'cancel-item'; item: OrderItemDto }
  | { kind: 'complete-item'; item: OrderItemDto };

/** Orders with their lines' progress; ordering doctors can cancel pending lines or complete bedside procedures. */
export function OrdersList({
  patientId,
  encounterId,
  status,
}: {
  patientId: string;
  encounterId: string | null;
  status?: ClinicalOrderStatus;
}) {
  const styles = useStyles();
  // Closing a dialog opened from a line's menu returns focus to that line's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canPlace = usePermission('clinical.orders.place');
  const [page, setPage] = useState(1);
  const [pending, setPending] = useState<PendingAction | null>(null);
  const orders = useQuery(orderQueries.list(patientId, { encounterId, status, page }));
  useLiveOrders(patientId);

  const act = useMutation({
    mutationFn: ({ action, reason }: { action: PendingAction; reason: string }) => {
      switch (action.kind) {
        case 'cancel-order':
          return ordersApi.cancel(action.order.id, reason);
        case 'cancel-item':
          return ordersApi.cancelItem(action.item.id, reason);
        case 'complete-item':
          return ordersApi.updateItemStatus(action.item.id, 'Completed', reason || null);
      }
    },
    onSuccess: (_, { action }) => {
      setPending(null);
      notify.success(action.kind === 'complete-item' ? 'Marked as done' : 'Cancelled');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
    onError: (error) => notify.error(error, 'The order could not be updated'),
  });

  if (orders.isLoading) {
    return <LoadingState />;
  }
  const items = orders.data?.items ?? [];
  if (items.length === 0) {
    return (
      <EmptyState
        title="No orders"
        description={
          encounterId
            ? 'Tests and procedures ordered in this encounter appear here.'
            : 'Lab, imaging and procedure orders appear here.'
        }
      />
    );
  }

  return (
    <div className={styles.orders}>
      {items.map((order) => {
        const open = order.items.filter((i) => i.status === 'Ordered');
        return (
          <section key={order.id} className={styles.order} aria-label={`Order ${order.orderNumber}`}>
            <div className={styles.orderHeader}>
              <span className={styles.orderTitle}>
                <Text weight="semibold" font="monospace">
                  {order.orderNumber}
                </Text>
                <StatusBadge status={order.status} />
                {order.priority !== 'Routine' && (
                  <Badge color={order.priority === 'Stat' ? 'danger' : 'warning'} appearance="filled">
                    {order.priority === 'Stat' ? 'STAT' : 'Urgent'}
                  </Badge>
                )}
                <Caption1 className={styles.muted}>
                  {formatDateTime(order.orderedAt)}
                  {order.orderingPractitionerName ? ` · ${order.orderingPractitionerName}` : ''}
                  {order.source === 'HealthCheckup' ? ' · health check-up' : ''}
                </Caption1>
              </span>
              {canPlace && open.length > 0 && order.status === 'Active' && (
                <Button
                  size="small"
                  icon={<DismissRegular />}
                  onClick={() => setPending({ kind: 'cancel-order', order })}
                >
                  Cancel pending
                </Button>
              )}
            </div>
            {order.clinicalNotes && <Caption1>{order.clinicalNotes}</Caption1>}
            {order.items.map((item) => (
              <div key={item.id} className={styles.item}>
                <span className={styles.itemName}>
                  <Text className={item.status === 'Cancelled' ? styles.cancelled : undefined}>{item.serviceName}</Text>
                  {item.quantity > 1 && <Caption1>× {item.quantity}</Caption1>}
                  <Caption1 className={styles.muted}>{serviceCategoryLabel[item.category]}</Caption1>
                  {item.notes && <Caption1 className={styles.muted}>· {item.notes}</Caption1>}
                  {item.cancelReason && <Caption1 className={styles.muted}>· {item.cancelReason}</Caption1>}
                </span>
                <span className={styles.orderTitle}>
                  <StatusBadge
                    status={item.status}
                    tone={lineTone(item.status)}
                    label={item.status === 'Ordered' ? 'Pending' : humanize(item.status)}
                  />
                  {canPlace && (item.status === 'Ordered' || item.status === 'InProgress') && (
                    <Menu>
                      <MenuTrigger disableButtonEnhancement>
                        <Button
                          size="small"
                          appearance="subtle"
                          icon={<MoreHorizontalRegular />}
                          aria-label={`Actions for ${item.serviceName}`}
                          {...restoreFocusTarget}
                        />
                      </MenuTrigger>
                      <MenuPopover>
                        <MenuList>
                          {(item.category === 'Procedure' ||
                            item.category === 'Surgery' ||
                            item.category === 'Dietary' ||
                            item.category === 'Consultation') && (
                            <MenuItem
                              icon={<CheckmarkRegular />}
                              onClick={() => setPending({ kind: 'complete-item', item })}
                            >
                              Mark as done
                            </MenuItem>
                          )}
                          {item.status === 'Ordered' && (
                            <MenuItem
                              icon={<DismissRegular />}
                              onClick={() => setPending({ kind: 'cancel-item', item })}
                            >
                              Cancel this line
                            </MenuItem>
                          )}
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  )}
                </span>
              </div>
            ))}
          </section>
        );
      })}
      {(orders.data?.totalCount ?? 0) > 20 && (
        <Pagination page={page} pageSize={20} totalCount={orders.data?.totalCount ?? 0} onPageChange={setPage} />
      )}
      <ReasonDialog
        open={pending !== null}
        title={
          pending?.kind === 'cancel-order'
            ? `Cancel the pending lines of ${pending.order.orderNumber}?`
            : pending?.kind === 'cancel-item'
              ? `Cancel ${pending.item.serviceName}?`
              : pending?.kind === 'complete-item'
                ? `Mark ${pending.item.serviceName} as done?`
                : ''
        }
        description={
          pending?.kind === 'complete-item'
            ? 'Record where the result or procedure note can be found (optional).'
            : 'The performing department and billing are told immediately.'
        }
        label={pending?.kind === 'complete-item' ? 'Result reference' : 'Reason'}
        optional={pending?.kind === 'complete-item'}
        maxLength={pending?.kind === 'complete-item' ? 64 : 300}
        confirmLabel={pending?.kind === 'complete-item' ? 'Mark as done' : 'Cancel order'}
        destructive={pending?.kind !== 'complete-item'}
        busy={act.isPending}
        onConfirm={(reason) => pending && act.mutate({ action: pending, reason })}
        onCancel={() => setPending(null)}
      />
    </div>
  );
}

/** Chart tab: all orders of the patient with a status filter, and a direct order composer for doctors. */
export function OrdersTab({ patientId }: { patientId: string }) {
  const canPlace = usePermission('clinical.orders.place');
  const [status, setStatus] = useState<ClinicalOrderStatus | ''>('');
  const [composing, setComposing] = useState(false);
  return (
    <>
      <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap', marginBottom: 12 }}>
        {canPlace && (
          <Button
            appearance={composing ? 'secondary' : 'primary'}
            icon={composing ? <DismissRegular /> : <AddRegular />}
            onClick={() => setComposing((v) => !v)}
          >
            {composing ? 'Close composer' : 'Order tests'}
          </Button>
        )}
        <Field label="Status">
          <Select value={status} onChange={(_, d) => setStatus(d.value as ClinicalOrderStatus | '')}>
            <option value="">All</option>
            <option value="Active">Active</option>
            <option value="Completed">Completed</option>
            <option value="Cancelled">Cancelled</option>
          </Select>
        </Field>
      </div>
      {composing && (
        <Section
          title="New order"
          description="Orders placed here are not linked to an encounter. Prefer ordering from the encounter."
        >
          <OrderComposer patientId={patientId} encounterId={null} />
        </Section>
      )}
      {composing && <div style={{ height: 12 }} />}
      <OrdersList patientId={patientId} encounterId={null} status={status || undefined} />
    </>
  );
}
