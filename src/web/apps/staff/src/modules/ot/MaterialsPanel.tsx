import {
  Badge,
  Button,
  Caption1,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Option,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ArrowClockwiseRegular, BoxRegular, DeleteRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  formatDateOnly,
  formatDateTime,
  formatInr,
  formatNumber,
  isApiError,
  todayIso,
  useDebouncedValue,
} from '@hc360/core';
import {
  ChoiceField,
  ConfirmDialog,
  DataTable,
  DateField,
  FormGrid,
  FullRow,
  NumberField,
  Section,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch, type UseFormReturn } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import {
  MATERIAL_KINDS,
  otApi,
  otKeys,
  otQueries,
  type OtStoreBatch,
  type OtStoreItem,
  type SurgeryDetail,
  type SurgeryMaterialDto,
} from './api';
import { materialKindLabel } from './labels';
import { batchForLot, batchLabel, issuableQuantity, materialsGate, otStoreProblem, priceForBatch } from './logic';
import { applyFormErrors, errorMessage } from './shared/errors';
import { ActionError, ChargeBadge } from './shared/feedback';

const useStyles = makeStyles({
  mono: { fontFamily: tokens.fontFamilyMonospace },
  muted: { color: tokens.colorNeutralForeground3 },
  warn: { color: tokens.colorPaletteDarkOrangeForeground1 },
  total: { display: 'flex', justifyContent: 'flex-end', gap: '12px', padding: '8px 16px' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

const materialSchema = z
  .object({
    kind: z.enum(MATERIAL_KINDS),
    source: z.enum(['store', 'outside']),
    supplyItemId: z.string().nullable(),
    itemName: z.string().trim().min(1, 'Enter the item name.').max(200),
    manufacturer: z.string().trim().max(200),
    lotNumber: z.string().trim().max(64),
    serialNumber: z.string().trim().max(64),
    expiryDate: z.string().nullable(),
    quantity: z.number().nullable(),
    unitPrice: z.number().nullable(),
  })
  .superRefine((v, ctx) => {
    if (v.source === 'store' && !v.supplyItemId) {
      ctx.addIssue({
        code: 'custom',
        path: ['supplyItemId'],
        message: 'Choose the item from the OT store, or record it as consignment / patient-supplied.',
      });
    }
    if (v.kind === 'Implant' && !v.lotNumber && !v.serialNumber) {
      ctx.addIssue({
        code: 'custom',
        path: ['lotNumber'],
        message: 'Enter the lot or serial number of the implant (needed for device recalls).',
      });
    }
    if (v.expiryDate && v.expiryDate < todayIso()) {
      ctx.addIssue({
        code: 'custom',
        path: ['expiryDate'],
        message: 'This item is past its expiry date and must not be used.',
      });
    }
    if (v.quantity === null || v.quantity <= 0 || v.quantity > 1000) {
      ctx.addIssue({ code: 'custom', path: ['quantity'], message: 'Enter a quantity from 1 to 1000.' });
    }
    if (v.unitPrice === null || v.unitPrice < 0 || v.unitPrice > 10_000_000) {
      ctx.addIssue({
        code: 'custom',
        path: ['unitPrice'],
        message: 'Enter the price per unit in rupees (0 if not charged).',
      });
    }
  });

type MaterialValues = z.infer<typeof materialSchema>;

/** Problems raised by the OT store while issuing or returning stock ("stock.insufficient", "stock.no_store"…). */
function isStockError(error: unknown): boolean {
  return (
    isApiError(error) && Boolean(error.code && (error.code.startsWith('stock.') || error.code.startsWith('store.')))
  );
}

/** Stock refusals in plain words, with what to do next. */
function StockError({ error }: { error: unknown }) {
  if (!isApiError(error)) {
    return null;
  }
  const next =
    error.code === 'stock.no_store'
      ? 'Ask the stores team to set up an OT store, or record the item as consignment / patient-supplied.'
      : error.code === 'stock.store_ambiguous'
        ? 'Ask the stores team to keep a single active OT store.'
        : error.code === 'stock.insufficient'
          ? 'Check the quantity, or ask the store to receive or transfer stock to the OT store first.'
          : error.code === 'stock.batch_expired'
            ? 'Use a batch that is in date.'
            : error.code === 'stock.batch_not_found'
              ? 'Choose one of the listed batches, or check the lot number on the pack.'
              : 'Retry, or check the item with the stores team.';
  return (
    <MessageBar intent="error" layout="multiline" politeness="assertive">
      <MessageBarBody>
        <MessageBarTitle>The OT store could not issue this item</MessageBarTitle>
        {error.userMessage} {next}
      </MessageBarBody>
    </MessageBar>
  );
}

/**
 * Implants and consumables used (OT-06). Items taken from the OT store are issued from its stock; consignment and
 * patient-supplied items are only recorded. Every line is billed at the actual price when it is recorded, and
 * implants keep manufacturer, lot and serial so every patient who received a recalled device can be traced.
 */
export function MaterialsPanel({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const materials = useQuery(otQueries.materials(booking.id));
  const canRecord = usePermission('ot.materials.record');
  const canRetry = usePermission('ot.bookings.manage');
  const gate = materialsGate(booking);
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<SurgeryMaterialDto | null>(null);

  const remove = useMutation({
    mutationFn: (material: SurgeryMaterialDto) => otApi.removeMaterial(booking.id, material.id),
    onSuccess: (_, material) => {
      setRemoving(null);
      notify.success(
        'Item removed',
        material.supplyItemId
          ? 'Its charge was withdrawn and the item returned to OT store stock.'
          : 'Its charge was withdrawn.',
      );
      void queryClient.invalidateQueries({ queryKey: otKeys.materials(booking.id) });
    },
  });

  const retry = useMutation({
    mutationFn: () => otApi.retryCharges(booking.id),
    onSuccess: (outcome) => {
      if (outcome.status === 'Failed') {
        notify.warning('Billing is still not available', outcome.error ?? undefined);
      } else {
        notify.success('Charges posted to the bill');
      }
      void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) });
      void queryClient.invalidateQueries({ queryKey: otKeys.materials(booking.id) });
    },
    onError: (error) => notify.error(error, 'Could not retry billing'),
  });

  const rows = materials.data ?? [];
  const total = rows.reduce((sum, row) => sum + row.amount, 0);
  const failed = rows.filter((row) => row.chargeStatus === 'Failed');

  return (
    <Section
      title="Implants and consumables"
      description="Items from the OT store are issued from its stock. Everything is billed at the actual price as soon as it is recorded."
      actions={
        canRecord && (
          <Button appearance="primary" icon={<AddRegular />} disabled={!gate.allowed} onClick={() => setAdding(true)}>
            Record item
          </Button>
        )
      }
      flush
    >
      {canRecord && !gate.allowed && (
        <div style={{ padding: '0 16px 8px' }}>
          <MessageBar intent="info">
            <MessageBarBody>{gate.reason}</MessageBarBody>
          </MessageBar>
        </div>
      )}
      {failed.length > 0 && (
        <div style={{ padding: '0 16px 8px' }}>
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>
                {failed.length} item{failed.length === 1 ? '' : 's'} could not be billed
              </MessageBarTitle>
              {failed[0]?.chargeError ?? 'Billing was not available.'} The items are recorded; retry when billing is
              available.
            </MessageBarBody>
            {canRetry && (
              <MessageBarActions>
                <Button
                  icon={retry.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowClockwiseRegular />}
                  disabled={retry.isPending}
                  onClick={() => retry.mutate()}
                >
                  Retry billing
                </Button>
              </MessageBarActions>
            )}
          </MessageBar>
        </div>
      )}
      <DataTable
        ariaLabel="Implants and consumables"
        rows={materials.data}
        loading={materials.isLoading}
        error={materials.error}
        onRetry={() => void materials.refetch()}
        getRowId={(m) => m.id}
        size="small"
        columns={[
          {
            key: 'kind',
            header: 'Type',
            width: 110,
            render: (m) => (
              <Badge appearance="tint" color={m.kind === 'Implant' ? 'brand' : 'informative'}>
                {materialKindLabel[m.kind]}
              </Badge>
            ),
          },
          {
            key: 'item',
            header: 'Item',
            render: (m) => (
              <span>
                <Text weight="semibold">{m.itemName}</Text>
                <Caption1 block className={styles.muted}>
                  {[m.manufacturer, m.supplyItemId ? 'From OT store' : 'Consignment / patient-supplied']
                    .filter(Boolean)
                    .join(' · ')}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'trace',
            header: 'Lot / serial',
            render: (m) =>
              m.lotNumber || m.serialNumber ? (
                <span className={styles.mono}>
                  {m.lotNumber ? `Lot ${m.lotNumber}` : ''}
                  {m.lotNumber && m.serialNumber ? ' · ' : ''}
                  {m.serialNumber ? `S/N ${m.serialNumber}` : ''}
                </span>
              ) : (
                '—'
              ),
          },
          { key: 'expiry', header: 'Expiry', priority: 'low', render: (m) => formatDateOnly(m.expiryDate) },
          { key: 'qty', header: 'Qty', align: 'end', width: 60, render: (m) => m.quantity },
          { key: 'amount', header: 'Amount', align: 'end', render: (m) => formatInr(m.amount) },
          {
            key: 'by',
            header: 'Recorded',
            priority: 'low',
            render: (m) => `${m.recordedByName ?? '—'} · ${formatDateTime(m.recordedAt)}`,
          },
          { key: 'bill', header: 'Billing', render: (m) => <ChargeBadge status={m.chargeStatus} /> },
        ]}
        rowActions={
          canRecord && gate.allowed
            ? (m) => (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label={`Remove ${m.itemName}`}
                  onClick={() => setRemoving(m)}
                />
              )
            : undefined
        }
        emptyTitle="Nothing recorded yet"
        emptyDescription={
          canRecord
            ? 'Record implants with their lot or serial number from the pack label, and consumables as they are opened.'
            : undefined
        }
      />
      {rows.length > 0 && (
        <div className={styles.total}>
          <Text>Total</Text>
          <Text weight="semibold">{formatInr(total)}</Text>
        </div>
      )}

      {adding && <AddMaterialDialog booking={booking} onClose={() => setAdding(false)} />}

      <ConfirmDialog
        open={removing !== null}
        title={`Remove ${removing?.itemName ?? 'this item'}?`}
        confirmLabel="Remove and withdraw charge"
        destructive
        busy={remove.isPending}
        onConfirm={() => removing && remove.mutate(removing)}
        onCancel={() => {
          setRemoving(null);
          remove.reset();
        }}
      >
        <Text block>
          Use this only for an item recorded by mistake. The charge of {formatInr(removing?.amount)} is withdrawn from
          the patient&apos;s bill
          {removing?.supplyItemId ? ' and the item goes back into OT store stock' : ''}.
        </Text>
        {isStockError(remove.error) ? <StockError error={remove.error} /> : <ActionError error={remove.error} />}
      </ConfirmDialog>
    </Section>
  );
}

function AddMaterialDialog({ booking, onClose }: { booking: SurgeryDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();

  const form = useForm<MaterialValues>({
    resolver: zodResolver(materialSchema),
    defaultValues: {
      kind: 'Implant',
      source: 'store',
      supplyItemId: null,
      itemName: '',
      manufacturer: '',
      lotNumber: '',
      serialNumber: '',
      expiryDate: null,
      quantity: 1,
      unitPrice: null,
    },
  });
  const [kind, source, quantity, unitPrice, lotNumber] = useWatch({
    control: form.control,
    name: ['kind', 'source', 'quantity', 'unitPrice', 'lotNumber'],
  });
  const [picked, setPicked] = useState<OtStoreItem | null>(null);
  // The MRP last filled in as the price: choosing another batch updates it, unless the user typed a price of their own.
  const [filledPrice, setFilledPrice] = useState<number | null>(null);
  // The first page of the OT store tells whether the facility has one usable OT store (422 otherwise); the picker reuses it.
  const store = useQuery({ ...otQueries.storeItems(''), enabled: source === 'store' });
  const storeProblem = otStoreProblem(store.error);

  const chooseBatch = (batch: OtStoreBatch) => {
    form.setValue('lotNumber', batch.batchNumber, { shouldValidate: true });
    form.setValue('expiryDate', batch.expiryDate, { shouldValidate: true });
    const price = priceForBatch(form.getValues('unitPrice'), filledPrice, batch.mrp);
    form.setValue('unitPrice', price);
    if (price === batch.mrp) {
      setFilledPrice(price);
    }
  };

  /** Lot and expiry filled from an OT store batch do not describe another item, nor a consignment or patient-supplied one. */
  const clearStoreBatch = () => {
    if (batchForLot(picked, form.getValues('lotNumber'))) {
      form.setValue('lotNumber', '');
      form.setValue('expiryDate', null);
    }
  };

  const pickItem = (item: OtStoreItem | null) => {
    clearStoreBatch();
    setPicked(item);
    if (!item) {
      return;
    }
    form.setValue('itemName', item.name, { shouldValidate: true });
    if (item.manufacturer) {
      form.setValue('manufacturer', item.manufacturer);
    }
    const first = item.batches[0];
    if (first) {
      chooseBatch(first);
    } else if (item.mrp !== null && form.getValues('unitPrice') === null) {
      form.setValue('unitPrice', item.mrp);
      setFilledPrice(item.mrp);
    }
  };

  const add = useMutation({
    mutationFn: (values: MaterialValues) =>
      otApi.addMaterial(booking.id, {
        kind: values.kind,
        supplyItemId: values.source === 'store' ? values.supplyItemId : null,
        itemName: values.itemName,
        manufacturer: values.manufacturer || null,
        lotNumber: values.lotNumber ? values.lotNumber.toUpperCase() : null,
        serialNumber: values.serialNumber ? values.serialNumber.toUpperCase() : null,
        expiryDate: values.expiryDate,
        quantity: values.quantity ?? 1,
        unitPrice: values.unitPrice ?? 0,
      }),
    onSuccess: (saved) => {
      notify.success(
        `${saved.itemName} recorded`,
        saved.chargeStatus === 'Failed'
          ? 'Billing failed; it can be retried.'
          : `${formatInr(saved.amount)} added to the bill${saved.supplyItemId ? ' and issued from the OT store' : ''}.`,
      );
      void queryClient.invalidateQueries({ queryKey: otKeys.materials(booking.id) });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const amount = (quantity ?? 0) * (unitPrice ?? 0);
  const storePick = source === 'store' ? picked : null;
  const chosenBatch = batchForLot(storePick, lotNumber);
  const issuable = storePick ? issuableQuantity(storePick, lotNumber) : null;
  const short = issuable !== null && (quantity ?? 0) > issuable;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <form onSubmit={form.handleSubmit((values) => add.mutate(values))} noValidate>
          <DialogBody>
            <DialogTitle>Record implant or consumable</DialogTitle>
            <DialogContent className={styles.body}>
              <FormGrid columns={2}>
                <FullRow>
                  <ChoiceField
                    control={form.control}
                    name="kind"
                    label="Type"
                    options={MATERIAL_KINDS.map((value) => ({ value, label: materialKindLabel[value] }))}
                  />
                </FullRow>
                <FullRow>
                  <Controller
                    control={form.control}
                    name="source"
                    render={({ field }) => (
                      <Field label="Where it came from">
                        <RadioGroup
                          layout="horizontal"
                          value={field.value}
                          onChange={(_, d) => {
                            field.onChange(d.value);
                            if (d.value === 'outside') {
                              clearStoreBatch();
                              form.setValue('supplyItemId', null);
                              setPicked(null);
                            }
                          }}
                        >
                          <Radio value="store" label="OT store (issued from stock)" />
                          <Radio value="outside" label="Consignment or patient-supplied" />
                        </RadioGroup>
                      </Field>
                    )}
                  />
                </FullRow>
                {source === 'store' && (
                  <FullRow>
                    {store.isPending ? (
                      <Spinner size="tiny" label="Opening the OT store…" labelPosition="after" />
                    ) : storeProblem ? (
                      <MessageBar intent="warning" layout="multiline">
                        <MessageBarBody>
                          <MessageBarTitle>
                            {storeProblem === 'ambiguous'
                              ? 'More than one OT store is active'
                              : 'No OT store is set up'}
                          </MessageBarTitle>
                          {storeProblem === 'ambiguous'
                            ? 'The server cannot tell which store to issue from. Ask the stores team to keep one active OT store. Until then, record the item as consignment / patient-supplied.'
                            : 'Ask the stores team to set one up under Inventory → Stores. Until then, record the item as consignment / patient-supplied.'}
                        </MessageBarBody>
                      </MessageBar>
                    ) : (
                      <StoreItemPicker form={form} picked={picked} onPicked={pickItem} />
                    )}
                  </FullRow>
                )}
                {storePick && storePick.batches.length > 0 && (
                  <FullRow>
                    <Field
                      label="Batch"
                      hint="The batch that expires first is chosen. Pick the lot printed on the pack you are using."
                    >
                      <RadioGroup
                        value={chosenBatch?.batchId ?? ''}
                        onChange={(_, d) => {
                          const batch = storePick.batches.find((b) => b.batchId === d.value);
                          if (batch) {
                            chooseBatch(batch);
                          }
                        }}
                      >
                        {storePick.batches.map((batch) => (
                          <Radio
                            key={batch.batchId}
                            value={batch.batchId}
                            label={batchLabel(batch, storePick.baseUnit)}
                          />
                        ))}
                      </RadioGroup>
                    </Field>
                  </FullRow>
                )}
                <FullRow>
                  <TextField
                    control={form.control}
                    name="itemName"
                    label="Item"
                    required
                    autoFocus={source === 'outside'}
                    maxLength={200}
                    placeholder="e.g. Cemented knee prosthesis, size 4"
                    hint={source === 'store' ? 'Filled from the store item; add the size if needed.' : undefined}
                  />
                </FullRow>
                <TextField control={form.control} name="manufacturer" label="Manufacturer" maxLength={200} />
                <DateField control={form.control} name="expiryDate" label="Expiry date" min={todayIso()} />
                <TextField
                  control={form.control}
                  name="lotNumber"
                  label="Lot / batch number"
                  maxLength={64}
                  hint={
                    storePick
                      ? 'Filled from the chosen batch: the OT store issues this lot.'
                      : kind === 'Implant'
                        ? 'Scan or type from the pack label. Lot or serial is required for implants.'
                        : 'From the pack label, if known.'
                  }
                />
                <TextField control={form.control} name="serialNumber" label="Serial number" maxLength={64} />
                <div>
                  <NumberField control={form.control} name="quantity" label="Quantity" required min={1} max={1000} />
                  {short && (
                    <Caption1 className={styles.warn}>
                      Only {formatNumber(issuable)} {storePick?.baseUnit}{' '}
                      {chosenBatch ? `of lot ${chosenBatch.batchNumber} ` : ''}in the OT store. The store will refuse a
                      larger issue.
                    </Caption1>
                  )}
                </div>
                <NumberField
                  control={form.control}
                  name="unitPrice"
                  label="Price per unit (₹)"
                  required
                  min={0}
                  step={0.01}
                />
              </FormGrid>
              <Text>
                Amount billed: <Text weight="semibold">{formatInr(amount)}</Text>
              </Text>
              {isStockError(add.error) ? <StockError error={add.error} /> : <ActionError error={add.error} />}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={add.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={add.isPending || (source === 'store' && (store.isPending || storeProblem !== null))}
                icon={add.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {source === 'store' ? 'Issue, record and bill' : 'Record and bill'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Searches the OT store: only items it can issue now, with the quantity available and the nearest expiry. */
function StoreItemPicker({
  form,
  picked,
  onPicked,
}: {
  form: UseFormReturn<MaterialValues>;
  picked: OtStoreItem | null;
  onPicked: (item: OtStoreItem | null) => void;
}) {
  const styles = useStyles();
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 300);
  const items = useQuery(otQueries.storeItems(search));

  return (
    <Controller
      control={form.control}
      name="supplyItemId"
      render={({ field, fieldState }) => {
        const problem =
          fieldState.error?.message ??
          (items.error ? errorMessage(items.error, 'The OT store could not be searched. Try again.') : undefined);
        return (
          <Field
            label="Item from the OT store"
            required
            validationMessage={problem}
            validationState={problem ? 'error' : 'none'}
            hint="Scan the item code or type its name. Only items the OT store can issue now are listed."
          >
            <Combobox
              placeholder="Scan or type to search the OT store"
              value={term || picked?.name || ''}
              selectedOptions={field.value ? [field.value] : []}
              onChange={(event) => {
                setTerm(event.target.value);
                if (!event.target.value) {
                  field.onChange(null);
                  onPicked(null);
                }
              }}
              onOptionSelect={(_, data) => {
                const item = items.data?.find((i) => i.itemId === data.optionValue) ?? null;
                field.onChange(item?.itemId ?? null);
                onPicked(item);
                setTerm('');
              }}
              expandIcon={items.isFetching ? <Spinner size="extra-tiny" aria-hidden /> : <BoxRegular />}
              clearable
              autoFocus
            >
              {(items.data ?? []).map((item) => (
                <Option key={item.itemId} value={item.itemId} text={item.name} disabled={item.available <= 0}>
                  <span>
                    {item.name} <span className={styles.muted}>· {item.code}</span>
                    <Caption1 block className={item.available <= 0 ? styles.warn : styles.muted}>
                      {item.available <= 0
                        ? 'Out of stock in the OT store'
                        : `${formatNumber(item.available)} ${item.baseUnit} in stock`}
                      {item.nearestExpiry ? ` · earliest expiry ${formatDateOnly(item.nearestExpiry)}` : ''}
                      {item.manufacturer ? ` · ${item.manufacturer}` : ''}
                    </Caption1>
                  </span>
                </Option>
              ))}
            </Combobox>
          </Field>
        );
      }}
    />
  );
}
