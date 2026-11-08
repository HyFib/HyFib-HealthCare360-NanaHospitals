import {
  Badge,
  Button,
  Caption1,
  Checkbox,
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
  MessageBarTitle,
  Spinner,
  Switch,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowSyncRegular,
  CheckmarkRegular,
  DismissRegular,
  MoreHorizontalRegular,
  SignatureRegular,
  StopRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, isApiError, useSession, type LookupItem } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  DataTable,
  FormGrid,
  LookupField,
  NumberField,
  StatusBadge,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
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
  ORDER_MODE_LABEL,
  ORDER_SOURCE_LABEL,
  searchWardDrugs,
  type MedicationOrderDto,
  type WardDrugDto,
} from '../nursingApi';
import { ReasonDialog } from '../ReasonDialog';
import { formatTimeOfDay, fromIstInput, toIstInput } from '../time';
import { countersignState, writesVerbalOrder } from '../verbalOrders';

/**
 * The medication chart (what the doctor ordered). E-prescriptions arrive on their own; ward orders are written here. A
 * verbal or telephone order, and a nurse's stop, show as awaiting the doctor's countersignature, and the doctor named is
 * offered Countersign (or Confirm stop and Decline stop) on their own orders.
 */
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
  const [answering, setAnswering] = useState<Countersigning | null>(null);
  const [declining, setDeclining] = useState<MedicationOrderDto | null>(null);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: nursingKeys.all });
  const stop = useMutation({
    mutationFn: ({ order, reason }: { order: MedicationOrderDto; reason: string }) =>
      nursingApi.stopOrder(order.id, reason),
    onSuccess: (order) => {
      if (order.stopPending) {
        notify.warning(
          `Stop of ${order.drugName} asked for`,
          `Its doses are held until ${order.orderedByName ?? 'the doctor'} countersigns the stop.`,
        );
      } else {
        notify.success(`${order.drugName} stopped`, 'Doses not yet due were cancelled.');
      }
      setStopping(null);
      refresh();
    },
  });
  const decline = useMutation({
    mutationFn: ({ order, reason }: { order: MedicationOrderDto; reason: string }) =>
      nursingApi.declineStop(order.id, reason, order.version),
    onSuccess: (order) => {
      notify.success(`Stop of ${order.drugName} declined`, 'The medicine goes on, and its doses are given again.');
      setDeclining(null);
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
                {o.isHighAlert && (
                  <Badge size="small" color="severe" appearance="tint" style={{ marginLeft: 6 }}>
                    High alert
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
          {
            key: 'source',
            header: 'From',
            priority: 'low',
            render: (o) => (
              <span>
                {o.mode === 'Written' ? ORDER_SOURCE_LABEL[o.source] : ORDER_MODE_LABEL[o.mode]}
                {o.isEmergency && (
                  <Badge size="small" color="danger" appearance="outline" style={{ marginLeft: 6 }}>
                    Emergency
                  </Badge>
                )}
                {o.orderedByName && <Caption1 block>{o.orderedByName}</Caption1>}
                {o.mode !== 'Written' && o.enteredByName && <Caption1 block>Written by {o.enteredByName}</Caption1>}
              </span>
            ),
          },
          {
            key: 'status',
            header: 'Status',
            width: 190,
            render: (o) => <OrderStatus order={o} />,
          },
        ]}
        rowActions={(o) => {
          const schedulable =
            canEdit && o.status === 'Active' && !o.isAsNeeded && o.roundTimes.length > 0 && !o.dosesHeld;
          const stoppable = canEdit && o.status === 'Active' && !o.stopPending;
          const countersign = o.canCountersign && o.awaitingCountersignature;
          const answerStop = o.canCountersign && o.stopPending;
          if (!schedulable && !stoppable && !countersign && !answerStop) {
            return null;
          }
          return (
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
                  {countersign && (
                    <MenuItem icon={<SignatureRegular />} onClick={() => setAnswering({ order: o, what: 'order' })}>
                      Countersign
                    </MenuItem>
                  )}
                  {answerStop && (
                    <MenuItem icon={<CheckmarkRegular />} onClick={() => setAnswering({ order: o, what: 'stop' })}>
                      Confirm stop
                    </MenuItem>
                  )}
                  {answerStop && (
                    <MenuItem icon={<DismissRegular />} onClick={() => setDeclining(o)}>
                      Decline stop
                    </MenuItem>
                  )}
                  {schedulable && (
                    <MenuItem
                      icon={extend.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowSyncRegular />}
                      onClick={() => extend.mutate(o)}
                    >
                      Schedule next 24 hours
                    </MenuItem>
                  )}
                  {stoppable && (
                    <MenuItem icon={<StopRegular />} onClick={() => setStopping(o)}>
                      Stop medicine
                    </MenuItem>
                  )}
                </MenuList>
              </MenuPopover>
            </Menu>
          );
        }}
        emptyTitle={activeOnly ? 'No active medicines' : 'No medicines charted'}
        emptyDescription="Signed in-patient e-prescriptions appear here automatically. Write a doctor's verbal or telephone order with Add ward order."
      />
      {adding && <AddOrderDialog admissionId={admissionId} onClose={() => setAdding(false)} />}
      <ReasonDialog
        open={stopping !== null}
        title={stopping ? `Stop ${stopping.drugName}?` : 'Stop medicine?'}
        description="A doctor's stop is made at once: doses not yet due are cancelled, overdue ones stay on the chart to be recorded. A nurse's stop waits for the doctor's countersignature, and the doses are held until then."
        placeholder="e.g. Rash after the second dose"
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
      <ReasonDialog
        open={declining !== null}
        title={declining ? `Decline the stop of ${declining.drugName}?` : 'Decline the stop?'}
        description={
          declining
            ? `${declining.stopRequestedByName ?? 'The nurse'} asked to stop it (${declining.stopRequestReason ?? ''}). Declined, the medicine goes on and its doses are given again; they are told why.`
            : undefined
        }
        placeholder="e.g. Still needed: continue for two more days"
        confirmLabel="Decline stop"
        busy={decline.isPending}
        error={decline.error}
        onConfirm={(reason) => declining && decline.mutate({ order: declining, reason })}
        onCancel={() => {
          setDeclining(null);
          decline.reset();
        }}
      />
      {answering && (
        <CountersignDialog countersigning={answering} onClose={() => setAnswering(null)} onDone={refresh} />
      )}
    </div>
  );
}

/** The order's status, with what waits on it for the doctor. */
function OrderStatus({ order: o }: { order: MedicationOrderDto }) {
  const waiting = countersignState(o);
  return (
    <span>
      <StatusBadge
        status={o.status}
        tone={o.status === 'Active' ? 'success' : o.status === 'Stopped' ? 'danger' : 'informative'}
      />
      {waiting && (
        <Badge size="small" color={waiting.overdue ? 'danger' : 'warning'} appearance="tint" style={{ marginLeft: 6 }}>
          {waiting.label}
        </Badge>
      )}
      {o.awaitingCountersignature && o.countersignDueAt && (
        <Caption1 block>
          {o.orderedByName ?? 'The doctor'} countersigns by {formatDateTime(o.countersignDueAt)}
          {o.dosesHeld && !o.stopPending ? '. No dose until then.' : ''}
        </Caption1>
      )}
      {o.stopPending && (
        <Caption1 block>
          Stop asked for by {o.stopRequestedByName ?? 'a nurse'}: {o.stopRequestReason}
        </Caption1>
      )}
      {o.countersignedAt && (
        <Caption1 block>
          Countersigned by {o.countersignedByName} · {formatDateTime(o.countersignedAt)}
        </Caption1>
      )}
      {o.stopReason && <Caption1 block>{o.stopReason}</Caption1>}
    </span>
  );
}

/** What the doctor answers: the verbal order itself, or a nurse's stop of it. */
export interface Countersigning {
  order: MedicationOrderDto;
  what: 'order' | 'stop';
}

/**
 * The doctor named countersigns a verbal or telephone order written in their name, or confirms a nurse's stop, from their
 * own sign-in: what they sign is shown first, and it is signed only as it was shown (the order's version).
 */
export function CountersignDialog({
  countersigning,
  onClose,
  onDone,
}: {
  countersigning: Countersigning;
  onClose: () => void;
  onDone: () => void;
}) {
  const notify = useNotify();
  const { order: o, what } = countersigning;
  const sign = useMutation({
    mutationFn: () =>
      what === 'order' ? nursingApi.countersign(o.id, o.version) : nursingApi.confirmStop(o.id, o.version),
    onSuccess: (order) => {
      notify.success(
        what === 'order' ? `${order.drugName} countersigned` : `${order.drugName} stopped`,
        what === 'order' ? undefined : 'The doses held since the stop was asked for are cancelled.',
      );
      onDone();
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !sign.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{what === 'order' ? 'Countersign this order?' : 'Countersign this stop?'}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Text weight="semibold">
              {o.drugName}
              {o.strength ? ` ${o.strength}` : ''} · {o.dose}
              {o.route ? ` · ${o.route}` : ''} · {o.frequency}
            </Text>
            {what === 'order' ? (
              <Caption1>
                {ORDER_MODE_LABEL[o.mode]} written in your name by {o.enteredByName ?? 'a nurse'}, read back to you
                {o.isEmergency ? ', as an emergency' : ''}. Countersigning makes it your signed order
                {o.dosesHeld ? ', and its doses go on the round' : ''}.
              </Caption1>
            ) : (
              <Caption1>
                {o.stopRequestedByName ?? 'A nurse'} asked to stop it: {o.stopRequestReason}. Countersigned, it stops in
                your name.
              </Caption1>
            )}
            {sign.error !== null && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  {isApiError(sign.error) ? sign.error.userMessage : 'It could not be countersigned.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={sign.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              icon={sign.isPending ? <Spinner size="tiny" aria-hidden /> : <SignatureRegular />}
              disabled={sign.isPending}
              onClick={() => sign.mutate()}
            >
              {what === 'order' ? 'Countersign' : 'Confirm stop'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

const orderSchema = z
  .object({
    drugItemId: z.string().nullable(),
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
    mode: z.enum(['Verbal', 'Telephone']).nullable(),
    readBack: z.boolean(),
    emergency: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.later && !fromIstInput(v.startAt)) {
      ctx.addIssue({ code: 'custom', path: ['startAt'], message: 'Enter the start time.' });
    }
  });

type OrderValues = z.infer<typeof orderSchema>;

/**
 * A ward order. A prescriber writing in their own name signs it at once. Anyone else writes the doctor's verbal or
 * telephone order: the doctor, how they gave it and the read-back are required, and the doctor countersigns it. A
 * medicine picked from the drug master takes its controlled and high-alert flags from it; one typed in is handled as
 * high-alert.
 */
function AddOrderDialog({ admissionId, onClose }: { admissionId: string; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const myPractitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const canSign = usePermission('clinical.prescriptions.sign');
  const [nowInput] = useState(() => toIstInput(new Date()));
  // The drugs the picker found, by id: the one chosen fills the name and says how its doses are handled.
  const [found, setFound] = useState<Record<string, WardDrugDto>>({});
  const form = useForm<OrderValues>({
    resolver: zodResolver(orderSchema),
    defaultValues: {
      drugItemId: null,
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
      mode: null,
      readBack: false,
      emergency: false,
    },
  });
  const later = useWatch({ control: form.control, name: 'later' });
  const drugItemId = useWatch({ control: form.control, name: 'drugItemId' });
  const orderedBy = useWatch({ control: form.control, name: 'orderedByPractitionerId' });
  const isControlledDrug = useWatch({ control: form.control, name: 'isControlledDrug' });
  const verbal = writesVerbalOrder(orderedBy, myPractitionerId, canSign);
  const drug = drugItemId ? (found[drugItemId] ?? null) : null;
  const highRisk = drug ? drug.isControlled || drug.isHighAlert || isControlledDrug : true;

  // A drug picked from the master names the medicine as the master does.
  useEffect(() => {
    if (drug) {
      form.setValue('drugName', drug.name, { shouldDirty: true, shouldValidate: true });
      form.setValue('genericName', drug.genericName ?? '', { shouldDirty: true });
      form.setValue('strength', drug.strength ?? '', { shouldDirty: true });
    }
  }, [drug, form]);

  const loadDrugs = async (search: string): Promise<LookupItem[]> => {
    const drugs = await searchWardDrugs(search);
    setFound((known) => ({ ...known, ...Object.fromEntries(drugs.map((d) => [d.id, d])) }));
    return drugs.map((d) => ({ id: d.id, code: d.code, name: d.strength ? `${d.name} ${d.strength}` : d.name }));
  };

  const add = useMutation({
    mutationFn: (v: OrderValues) =>
      nursingApi.addOrder(admissionId, {
        drugItemId: v.drugItemId,
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
        mode: verbal ? v.mode : null,
        readBack: verbal && v.readBack,
        emergency: verbal && v.emergency,
      }),
    onSuccess: (order) => {
      notify.success(
        `${order.drugName} added to the chart`,
        order.awaitingCountersignature
          ? order.dosesHeld
            ? `No dose is given until ${order.orderedByName ?? 'the doctor'} countersigns it.`
            : `${order.orderedByName ?? 'The doctor'} countersigns it by ${formatDateTime(order.countersignDueAt ?? order.startAt)}.`
          : order.isAsNeeded
            ? 'Record each dose when it is given.'
            : 'The next 24 hours of doses are scheduled.',
      );
      void queryClient.invalidateQueries({ queryKey: nursingKeys.all });
      onClose();
    },
    onError: (error) => applyServerErrors(error, form.setError),
  });

  const submit = (v: OrderValues) => {
    if (verbal && !v.orderedByPractitionerId && !myPractitionerId) {
      form.setError('orderedByPractitionerId', { message: 'Choose the doctor who gave the order.' });
      return;
    }
    if (verbal && !v.mode) {
      form.setError('mode', { message: 'Say whether the doctor gave it face to face or on the telephone.' });
      return;
    }
    if (verbal && !v.readBack) {
      form.setError('readBack', { message: 'Read the order back to the doctor before charting it.' });
      return;
    }
    add.mutate(v);
  };

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: add.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <form onSubmit={form.handleSubmit(submit)} noValidate>
          <DialogBody>
            <DialogTitle>Add ward medication order</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>
                {verbal
                  ? "You are writing a doctor's verbal or telephone order: read it back to them, and they countersign it from their own sign-in."
                  : 'Written in your own name, the order is signed at once.'}
              </Caption1>
              <LookupField
                control={form.control}
                name="drugItemId"
                label="Medicine from the drug master"
                load={loadDrugs}
                placeholder="Type the name, generic or code"
                hint="Its controlled and high-alert flags come from the drug master"
              />
              {drug ? (
                (drug.isControlled || drug.isHighAlert) && (
                  <MessageBar intent="warning" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>Witnessed</MessageBarTitle>
                      {drug.isControlled ? 'A controlled drug' : 'A high-alert medicine'}: every dose is witnessed by a
                      second nurse{verbal ? ', and a verbal order gives no dose until the doctor countersigns it' : ''}.
                    </MessageBarBody>
                  </MessageBar>
                )
              ) : (
                <MessageBar intent="info" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Handled as high-alert</MessageBarTitle>
                    Not picked from the drug master: it is handled as high-alert, so every dose is witnessed
                    {verbal ? ', and a verbal order gives no dose until the doctor countersigns it' : ''}.
                  </MessageBarBody>
                </MessageBar>
              )}
              <FormGrid columns={3} minColumnWidth={180}>
                <TextField control={form.control} name="drugName" label="Medicine" required maxLength={200} />
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
                  required={myPractitionerId === null}
                  load={searchPractitioners}
                  hint={myPractitionerId ? 'Blank: you' : 'The doctor who gave the order'}
                />
              </FormGrid>
              {verbal && (
                <div
                  style={{
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 8,
                    padding: 12,
                    border: `1px solid ${tokens.colorNeutralStroke2}`,
                    borderRadius: tokens.borderRadiusMedium,
                  }}
                >
                  <ChoiceField
                    control={form.control}
                    name="mode"
                    label="How the doctor gave the order"
                    required
                    options={[
                      { value: 'Verbal', label: 'Verbal (face to face)' },
                      { value: 'Telephone', label: 'Telephone' },
                    ]}
                  />
                  <Controller
                    control={form.control}
                    name="readBack"
                    render={({ field, fieldState }) => (
                      <Field
                        required
                        validationMessage={fieldState.error?.message}
                        validationState={fieldState.error ? 'error' : 'none'}
                      >
                        <Checkbox
                          checked={field.value}
                          onChange={(_, d) => field.onChange(d.checked === true)}
                          label="I read the order back to the doctor, who confirmed it"
                        />
                      </Field>
                    )}
                  />
                  {highRisk && (
                    <SwitchField
                      control={form.control}
                      name="emergency"
                      label="Emergency: give it before the doctor countersigns (each dose witnessed)"
                      hint="Only for an emergency verbal order. The order stays flagged until countersigned."
                    />
                  )}
                </div>
              )}
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
              <Button onClick={guard.requestClose} disabled={add.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={add.isPending}
                icon={add.isPending ? <Spinner size="tiny" aria-hidden /> : <AddRegular />}
              >
                {verbal ? 'Add verbal order' : 'Add to chart'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
