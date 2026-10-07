import {
  Badge,
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
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { AddRegular, ArrowSyncRegular, MoreHorizontalRegular, StopRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  DataTable,
  FormGrid,
  LookupField,
  NumberField,
  StatusBadge,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { searchPractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import {
  COMMON_FREQUENCIES,
  COMMON_ROUTES,
  nursingApi,
  nursingKeys,
  nursingQueries,
  ORDER_SOURCE_LABEL,
  type MedicationOrderDto,
} from '../nursingApi';
import { ReasonDialog } from '../ReasonDialog';
import { formatTimeOfDay, fromIstInput, toIstInput } from '../time';

/** The medication chart (what the doctor ordered). E-prescriptions arrive on their own; ward orders are transcribed here. */
export function MedicationOrders({ admissionId }: { admissionId: string }) {
  const notify = useNotify();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const queryClient = useQueryClient();
  const canEdit = usePermission('nursing.mar');
  const [activeOnly, setActiveOnly] = useState(true);
  const orders = useQuery(nursingQueries.orders(admissionId, activeOnly));
  const [adding, setAdding] = useState(false);
  const [stopping, setStopping] = useState<MedicationOrderDto | null>(null);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: nursingKeys.admission(admissionId) });
  const stop = useMutation({
    mutationFn: ({ order, reason }: { order: MedicationOrderDto; reason: string }) =>
      nursingApi.stopOrder(order.id, reason),
    onSuccess: (order) => {
      notify.success(`${order.drugName} stopped`, 'Doses not yet due were cancelled.');
      setStopping(null);
      refresh();
    },
  });
  const extend = useMutation({
    mutationFn: (order: MedicationOrderDto) => nursingApi.extendSchedule(order.id),
    onSuccess: (result, order) => {
      notify.success(
        result.dosesAdded
          ? `${result.dosesAdded} doses of ${order.drugName} scheduled`
          : 'The next 24 hours are already on the chart',
      );
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not extend the schedule'),
  });

  return (
    <div>
      <div
        style={{
          display: 'flex',
          gap: 8,
          flexWrap: 'wrap',
          alignItems: 'center',
          justifyContent: 'space-between',
          marginBottom: 12,
        }}
      >
        <Switch checked={activeOnly} onChange={(_, d) => setActiveOnly(d.checked)} label="Active medicines only" />
        {canEdit && (
          <Button icon={<AddRegular />} onClick={() => setAdding(true)}>
            Add ward order
          </Button>
        )}
      </div>
      <DataTable
        ariaLabel="Medication orders"
        rows={orders.data}
        loading={orders.isLoading}
        error={orders.error}
        onRetry={() => void orders.refetch()}
        getRowId={(o) => o.id}
        size="small"
        columns={[
          {
            key: 'drug',
            header: 'Medicine',
            render: (o) => (
              <span>
                <Text weight="semibold">
                  {o.drugName}
                  {o.strength ? ` ${o.strength}` : ''}
                </Text>
                {o.isControlledDrug && (
                  <Badge size="small" color="danger" appearance="tint" style={{ marginLeft: 6 }}>
                    Controlled
                  </Badge>
                )}
                {o.genericName && <Caption1 block>{o.genericName}</Caption1>}
                {o.instructions && <Caption1 block>{o.instructions}</Caption1>}
              </span>
            ),
          },
          {
            key: 'dose',
            header: 'Dose',
            render: (o) => (
              <span>
                {o.dose}
                {o.route ? ` · ${o.route}` : ''} · <strong>{o.frequency}</strong>
                {o.roundTimes.length > 0 && (
                  <Caption1 block>At {o.roundTimes.map(formatTimeOfDay).join(', ')}</Caption1>
                )}
                {o.isAsNeeded && <Caption1 block>Only when needed (not scheduled)</Caption1>}
              </span>
            ),
          },
          {
            key: 'period',
            header: 'Period',
            priority: 'low',
            render: (o) => (
              <span>
                {formatDateTime(o.startAt)}
                <Caption1 block>
                  {o.endAt
                    ? `until ${formatDateTime(o.endAt)}`
                    : o.durationDays
                      ? `${o.durationDays} days`
                      : 'Until stopped'}
                </Caption1>
              </span>
            ),
          },
          { key: 'source', header: 'From', priority: 'low', render: (o) => ORDER_SOURCE_LABEL[o.source] },
          {
            key: 'status',
            header: 'Status',
            width: 120,
            render: (o) => (
              <span>
                <StatusBadge
                  status={o.status}
                  tone={o.status === 'Active' ? 'success' : o.status === 'Stopped' ? 'danger' : 'informative'}
                />
                {o.stopReason && <Caption1 block>{o.stopReason}</Caption1>}
              </span>
            ),
          },
        ]}
        rowActions={
          canEdit
            ? (o) =>
                o.status === 'Active' ? (
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<MoreHorizontalRegular />}
                        aria-label={`Actions for ${o.drugName}`}
                        {...restoreFocusTarget}
                      />
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList>
                        {!o.isAsNeeded && o.roundTimes.length > 0 && (
                          <MenuItem
                            icon={extend.isPending ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
                            onClick={() => extend.mutate(o)}
                          >
                            Schedule next 24 hours
                          </MenuItem>
                        )}
                        <MenuItem icon={<StopRegular />} onClick={() => setStopping(o)}>
                          Stop medicine
                        </MenuItem>
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                ) : null
            : undefined
        }
        emptyTitle={activeOnly ? 'No active medicines' : 'No medicines charted'}
        emptyDescription="Signed in-patient e-prescriptions appear here automatically. Transcribe verbal or paper orders with Add ward order."
      />
      {adding && <AddOrderDialog admissionId={admissionId} onClose={() => setAdding(false)} />}
      <ReasonDialog
        open={stopping !== null}
        title={stopping ? `Stop ${stopping.drugName}?` : 'Stop medicine?'}
        description="Doses that are not yet due are cancelled. Overdue doses stay on the chart to be recorded."
        placeholder="e.g. Stopped by Dr. Rao on ward round"
        confirmLabel="Stop medicine"
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

const orderSchema = z
  .object({
    drugName: z.string().trim().min(1, 'Enter the medicine name.').max(200),
    genericName: z.string().trim().max(200),
    strength: z.string().trim().max(64),
    dose: z.string().trim().min(1, 'Enter the dose, e.g. 1 tablet or 500 mg.').max(64),
    route: z.string().trim().max(32),
    frequency: z.string().trim().min(1, 'Choose how often.').max(32),
    durationDays: z.number().int().min(1, 'At least 1 day.').max(90, 'At most 90 days.').nullable(),
    later: z.boolean(),
    startAt: z.string(),
    instructions: z.string().trim().max(500),
    isControlledDrug: z.boolean(),
    orderedByPractitionerId: z.string().nullable(),
  })
  .superRefine((v, ctx) => {
    if (v.later && !fromIstInput(v.startAt)) {
      ctx.addIssue({ code: 'custom', path: ['startAt'], message: 'Enter the start time.' });
    }
  });

type OrderValues = z.infer<typeof orderSchema>;

function AddOrderDialog({ admissionId, onClose }: { admissionId: string; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [nowInput] = useState(() => toIstInput(new Date()));
  const form = useForm<OrderValues>({
    resolver: zodResolver(orderSchema),
    defaultValues: {
      drugName: '',
      genericName: '',
      strength: '',
      dose: '',
      route: 'Oral',
      frequency: 'BD',
      durationDays: 5,
      later: false,
      startAt: nowInput,
      instructions: '',
      isControlledDrug: false,
      orderedByPractitionerId: null,
    },
  });
  const later = useWatch({ control: form.control, name: 'later' });
  const add = useMutation({
    mutationFn: (v: OrderValues) =>
      nursingApi.addOrder(admissionId, {
        drugItemId: null,
        drugName: v.drugName,
        genericName: v.genericName || null,
        strength: v.strength || null,
        dose: v.dose,
        route: v.route || null,
        frequency: v.frequency,
        durationDays: v.durationDays,
        startAt: v.later ? fromIstInput(v.startAt) : null,
        instructions: v.instructions || null,
        isControlledDrug: v.isControlledDrug,
        orderedByPractitionerId: v.orderedByPractitionerId,
      }),
    onSuccess: (order) => {
      notify.success(
        `${order.drugName} added to the chart`,
        order.isAsNeeded ? 'Record each dose when it is given.' : 'The next 24 hours of doses are scheduled.',
      );
      void queryClient.invalidateQueries({ queryKey: nursingKeys.admission(admissionId) });
      onClose();
    },
    onError: (error) => applyServerErrors(error, form.setError),
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !add.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <form onSubmit={form.handleSubmit((v) => add.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>Add ward medication order</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>
                For verbal or paper orders. The prescribing doctor should sign the e-prescription at the next round.
              </Caption1>
              <FormGrid columns={3} minColumnWidth={180}>
                <TextField control={form.control} name="drugName" label="Medicine" required maxLength={200} autoFocus />
                <TextField
                  control={form.control}
                  name="strength"
                  label="Strength"
                  maxLength={64}
                  placeholder="e.g. 500 mg"
                />
                <TextField control={form.control} name="genericName" label="Generic name" maxLength={200} />
                <TextField
                  control={form.control}
                  name="dose"
                  label="Dose"
                  required
                  maxLength={64}
                  placeholder="e.g. 1 tablet"
                />
                <Controller
                  control={form.control}
                  name="route"
                  render={({ field }) => (
                    <Field label="Route">
                      <Input
                        value={field.value}
                        onChange={(_, d) => field.onChange(d.value)}
                        maxLength={32}
                        list="order-routes"
                      />
                      <datalist id="order-routes">
                        {COMMON_ROUTES.map((r) => (
                          <option key={r} value={r} />
                        ))}
                      </datalist>
                    </Field>
                  )}
                />
                <Controller
                  control={form.control}
                  name="frequency"
                  render={({ field, fieldState }) => (
                    <Field
                      label="How often"
                      required
                      validationMessage={fieldState.error?.message}
                      validationState={fieldState.error ? 'error' : 'none'}
                      hint="OD, BD, TDS, 1-0-1, Q6H, SOS…"
                    >
                      <Input
                        value={field.value}
                        onChange={(_, d) => field.onChange(d.value)}
                        maxLength={32}
                        list="order-frequencies"
                      />
                      <datalist id="order-frequencies">
                        {COMMON_FREQUENCIES.map((f) => (
                          <option key={f.value} value={f.value}>
                            {f.label}
                          </option>
                        ))}
                      </datalist>
                    </Field>
                  )}
                />
                <NumberField
                  control={form.control}
                  name="durationDays"
                  label="For (days)"
                  min={1}
                  max={90}
                  hint="Blank: until stopped"
                />
                <LookupField
                  control={form.control}
                  name="orderedByPractitionerId"
                  label="Ordered by"
                  load={searchPractitioners}
                  hint="Defaults to you if you are a doctor"
                />
              </FormGrid>
              <TextAreaField
                control={form.control}
                name="instructions"
                label="Instructions"
                rows={2}
                maxLength={500}
                placeholder="e.g. After food; check pulse before giving"
              />
              <SwitchField
                control={form.control}
                name="isControlledDrug"
                label="Controlled drug (Schedule H1 / X, narcotic): needs a witness when given"
              />
              <SwitchField control={form.control} name="later" label="Start later or earlier than now" />
              {later && (
                <Controller
                  control={form.control}
                  name="startAt"
                  render={({ field, fieldState }) => (
                    <Field
                      label="Start at"
                      required
                      validationMessage={fieldState.error?.message}
                      validationState={fieldState.error ? 'error' : 'none'}
                      hint="Within the last 24 hours or the next 7 days"
                    >
                      <Input type="datetime-local" value={field.value} onChange={(_, d) => field.onChange(d.value)} />
                    </Field>
                  )}
                />
              )}
              {add.error && !(isApiError(add.error) && add.error.isValidation) && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(add.error) ? add.error.userMessage : 'The order could not be added.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={add.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={add.isPending}
                icon={add.isPending ? <Spinner size="tiny" /> : <AddRegular />}
              >
                Add to chart
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
