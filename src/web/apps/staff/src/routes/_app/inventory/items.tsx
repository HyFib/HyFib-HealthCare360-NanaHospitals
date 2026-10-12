import {
  Badge,
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  SearchBox,
  Select,
  Spinner,
  Subtitle2,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, useDebouncedValue } from '@hc360/core';
import {
  applyServerErrors,
  DataTable,
  FilterBar,
  FormGrid,
  NumberField,
  Page,
  PageHeader,
  Section,
  SelectField,
  SwitchField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import {
  inventoryApi,
  inventoryKeys,
  inventoryQueries,
  type ItemDetail,
  type ItemListItem,
  type SaveItemRequest,
} from '@/modules/inventory/api';
import { formatPercent } from '@/modules/inventory/format';
import { GST_RATES, isStandardGstRate } from '@/modules/inventory/gst';
import {
  DRUG_SCHEDULES,
  ITEM_TYPES,
  itemTypeLabel,
  requiresPrescription,
  requiresRegister,
  scheduleHint,
  scheduleLabel,
  VED_CLASSES,
  vedLabel,
  type DrugSchedule,
  type ItemType,
} from '@/modules/inventory/labels';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { ItemFlags } from '@/modules/inventory/StockBits';

interface ItemsSearch {
  q?: string;
  type?: ItemType;
  categoryId?: string;
  schedule?: DrugSchedule;
  controlled?: boolean;
  inactive?: boolean;
  page?: number;
  edit?: string;
}

export const Route = createFileRoute('/_app/inventory/items')({
  validateSearch: (s: Record<string, unknown>): ItemsSearch => ({
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    type: ITEM_TYPES.includes(s.type as ItemType) ? (s.type as ItemType) : undefined,
    categoryId: typeof s.categoryId === 'string' && s.categoryId ? s.categoryId : undefined,
    schedule: DRUG_SCHEDULES.includes(s.schedule as DrugSchedule) ? (s.schedule as DrugSchedule) : undefined,
    controlled: s.controlled === true || s.controlled === 'true' ? true : undefined,
    inactive: s.inactive === true || s.inactive === 'true' ? true : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
    edit: typeof s.edit === 'string' && s.edit ? s.edit : undefined,
  }),
  component: ItemsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  rules: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

/** Item master (drugs, consumables, implants, linen…) with HSN/GST, the D&C schedule and reorder levels. */
function ItemsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(S.itemsManage);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const categories = useQuery(inventoryQueries.categories());

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const items = useQuery(
    inventoryQueries.items({
      search: search.q,
      type: search.type,
      categoryId: search.categoryId,
      schedule: search.schedule,
      controlledOnly: search.controlled,
      includeInactive: search.inactive,
      page: search.page,
    }),
  );
  const set = (patch: Partial<ItemsSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Item master"
        subtitle="Medicines, consumables and other stock items with HSN, GST, drug schedule and reorder levels"
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => set({ edit: 'new' })}>
              Add item
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 260px' }}>
              <SearchBox
                value={term}
                onChange={(_, d) => setTerm(d.value)}
                placeholder="Name, generic name, code or barcode"
                autoFocus
              />
            </Field>
            <Field label="Type">
              <Select
                value={search.type ?? ''}
                onChange={(_, d) => set({ type: (d.value || undefined) as ItemType | undefined })}
              >
                <option value="">All</option>
                {ITEM_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {itemTypeLabel[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Category">
              <Select value={search.categoryId ?? ''} onChange={(_, d) => set({ categoryId: d.value || undefined })}>
                <option value="">All</option>
                {(categories.data ?? []).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Schedule">
              <Select
                value={search.schedule ?? ''}
                onChange={(_, d) => set({ schedule: (d.value || undefined) as DrugSchedule | undefined })}
              >
                <option value="">All</option>
                {DRUG_SCHEDULES.map((s) => (
                  <option key={s} value={s}>
                    {scheduleLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={search.controlled === true}
              onChange={(_, d) => set({ controlled: d.checked || undefined })}
              label="Controlled only"
            />
            <Switch
              checked={search.inactive === true}
              onChange={(_, d) => set({ inactive: d.checked || undefined })}
              label="Show inactive"
            />
          </FilterBar>
        </div>
        <DataTable<ItemListItem>
          ariaLabel="Items"
          rows={items.data?.items}
          loading={items.isLoading}
          error={items.error}
          onRetry={() => void items.refetch()}
          getRowId={(i) => i.id}
          onRowClick={(i) => set({ edit: i.id })}
          size="small"
          columns={[
            { key: 'code', header: 'Code', width: 110, render: (i) => <Text font="monospace">{i.code}</Text> },
            {
              key: 'name',
              header: 'Item',
              render: (i) => (
                <span>
                  <Text weight="semibold">{i.name}</Text>{' '}
                  <ItemFlags schedule={i.schedule} isControlled={i.isControlled} isColdChain={i.isColdChain} />
                  {!i.isActive && (
                    <Badge size="small" appearance="tint" style={{ marginLeft: 6 }}>
                      Inactive
                    </Badge>
                  )}
                  <Caption1 block className={styles.muted}>
                    {[i.genericName, i.strength, i.dosageForm].filter(Boolean).join(' · ')}
                  </Caption1>
                </span>
              ),
            },
            { key: 'type', header: 'Type', priority: 'low', render: (i) => itemTypeLabel[i.type] },
            { key: 'category', header: 'Category', priority: 'low', render: (i) => i.categoryName ?? '—' },
            { key: 'gst', header: 'GST', width: 70, align: 'end', render: (i) => formatPercent(i.gstRate) },
            {
              key: 'units',
              header: 'Units',
              priority: 'low',
              render: (i) => (i.purchaseUnit === i.baseUnit ? i.baseUnit : `${i.baseUnit} / ${i.purchaseUnit}`),
            },
            {
              key: 'reorder',
              header: 'Reorder at',
              align: 'end',
              priority: 'low',
              render: (i) => (i.reorderLevel > 0 ? i.reorderLevel.toLocaleString('en-IN') : '—'),
            },
          ]}
          emptyTitle={search.q ? 'No item matches' : 'No items yet'}
          emptyDescription={
            canManage ? 'Add items with their HSN code, GST rate and drug schedule before receiving stock.' : undefined
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={items.data?.totalCount}
          onPageChange={(p) => void navigate({ search: (prev) => ({ ...prev, page: p > 1 ? p : undefined }) })}
        />
      </Section>
      {search.edit && (
        <ItemDrawer
          key={search.edit}
          itemId={search.edit === 'new' ? null : search.edit}
          canManage={canManage}
          onClose={() => set({ edit: undefined })}
        />
      )}
    </Page>
  );
}

const itemSchema = z
  .object({
    code: z
      .string()
      .trim()
      .min(1, 'Enter the item code.')
      .max(32)
      .regex(/^[A-Za-z0-9\-_/]+$/, "Use letters, digits, '-', '_' or '/'."),
    name: z.string().trim().min(1, 'Enter the item name.').max(200),
    genericName: z.string().trim().max(200),
    brandName: z.string().trim().max(120),
    type: z.enum(ITEM_TYPES),
    categoryId: z.string().nullable(),
    strength: z.string().trim().max(60),
    dosageForm: z.string().trim().max(60),
    packSize: z.string().trim().max(60),
    baseUnit: z.string().trim().min(1, 'Enter the unit you issue in, e.g. TAB, ML, NOS.').max(16),
    purchaseUnit: z.string().trim().min(1, 'Enter the unit you buy in, e.g. STRIP, BOX.').max(16),
    purchaseUnitFactor: z
      .number({ error: 'Enter how many issue units make one purchase unit.' })
      .positive('Must be more than 0.')
      .max(100_000),
    hsnCode: z
      .string()
      .trim()
      .refine((v) => !v || /^[0-9]{4}([0-9]{2}){0,2}$/.test(v), 'HSN code has 4, 6 or 8 digits.'),
    gstRate: z.string().refine((v) => isStandardGstRate(Number(v)), 'Use a notified GST rate.'),
    schedule: z.enum(DRUG_SCHEDULES),
    isControlled: z.boolean(),
    isColdChain: z.boolean(),
    vedClass: z.enum(VED_CLASSES),
    reorderLevel: z.number({ error: 'Enter a number (0 if none).' }).min(0),
    reorderQuantity: z.number({ error: 'Enter a number (0 if none).' }).min(0),
    minimumStock: z.number({ error: 'Enter a number (0 if none).' }).min(0),
    maximumStock: z.number({ error: 'Enter a number (0 if none).' }).min(0),
    manufacturer: z.string().trim().max(120),
    barcode: z.string().trim().max(64),
    isActive: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.maximumStock > 0 && v.minimumStock > v.maximumStock) {
      ctx.addIssue({ code: 'custom', path: ['minimumStock'], message: 'The minimum cannot be above the maximum.' });
    }
  });

type ItemValues = z.infer<typeof itemSchema>;

function toForm(item: ItemDetail | null): ItemValues {
  return {
    code: item?.code ?? '',
    name: item?.name ?? '',
    genericName: item?.genericName ?? '',
    brandName: item?.brandName ?? '',
    type: item?.type ?? 'Drug',
    categoryId: item?.categoryId ?? null,
    strength: item?.strength ?? '',
    dosageForm: item?.dosageForm ?? '',
    packSize: item?.packSize ?? '',
    baseUnit: item?.baseUnit ?? 'NOS',
    purchaseUnit: item?.purchaseUnit ?? 'NOS',
    purchaseUnitFactor: item?.purchaseUnitFactor ?? 1,
    hsnCode: item?.hsnCode ?? '',
    gstRate: String(item?.gstRate ?? 5),
    schedule: item?.schedule ?? 'None',
    isControlled: item?.isControlled ?? false,
    isColdChain: item?.isColdChain ?? false,
    vedClass: item?.vedClass ?? 'Essential',
    reorderLevel: item?.reorderLevel ?? 0,
    reorderQuantity: item?.reorderQuantity ?? 0,
    minimumStock: item?.minimumStock ?? 0,
    maximumStock: item?.maximumStock ?? 0,
    manufacturer: item?.manufacturer ?? '',
    barcode: item?.barcode ?? '',
    isActive: item?.isActive ?? true,
  };
}

function ItemDrawer({
  itemId,
  canManage,
  onClose,
}: {
  itemId: string | null;
  canManage: boolean;
  onClose: () => void;
}) {
  const existing = useQuery({ ...inventoryQueries.item(itemId ?? ''), enabled: itemId !== null, staleTime: 0 });
  if (itemId && !existing.data) {
    return (
      <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
        <DrawerBody>
          {existing.error ? (
            <MessageBar intent="error">
              <MessageBarBody>
                {isApiError(existing.error) ? existing.error.userMessage : 'Could not load the item.'}
              </MessageBarBody>
            </MessageBar>
          ) : (
            <Spinner label="Loading…" />
          )}
        </DrawerBody>
      </OverlayDrawer>
    );
  }
  return <ItemEditor item={existing.data ?? null} canManage={canManage} onClose={onClose} />;
}

function ItemEditor({
  item,
  canManage,
  onClose,
}: {
  item: ItemDetail | null;
  canManage: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const categories = useQuery(inventoryQueries.categories(true));
  const form = useForm<ItemValues>({
    resolver: zodResolver(itemSchema),
    defaultValues: toForm(item),
    mode: 'onTouched',
  });
  const [schedule, isControlled, type, baseUnit, purchaseUnit] = useWatch({
    control: form.control,
    name: ['schedule', 'isControlled', 'type', 'baseUnit', 'purchaseUnit'],
  });
  const readOnly = !canManage;

  const save = useMutation({
    mutationFn: (values: ItemValues) => {
      const body: SaveItemRequest = {
        code: values.code,
        name: values.name,
        genericName: values.genericName || null,
        brandName: values.brandName || null,
        type: values.type,
        categoryId: values.categoryId,
        strength: values.strength || null,
        dosageForm: values.dosageForm || null,
        packSize: values.packSize || null,
        baseUnit: values.baseUnit.toUpperCase(),
        purchaseUnit: values.purchaseUnit.toUpperCase(),
        purchaseUnitFactor: values.purchaseUnitFactor,
        hsnCode: values.hsnCode || null,
        gstRate: Number(values.gstRate),
        schedule: values.schedule,
        isControlled: values.isControlled,
        isColdChain: values.isColdChain,
        vedClass: values.vedClass,
        reorderLevel: values.reorderLevel,
        reorderQuantity: values.reorderQuantity,
        minimumStock: values.minimumStock,
        maximumStock: values.maximumStock,
        manufacturer: values.manufacturer || null,
        barcode: values.barcode || null,
        isActive: values.isActive,
        version: item?.version ?? null,
      };
      return inventoryApi.saveItem(item?.id ?? null, body);
    },
    onSuccess: () => {
      notify.success(item ? 'Item saved' : 'Item added');
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.items });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'item.duplicate_code') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (isApiError(error) && error.code === 'item.duplicate_barcode') {
        form.setError('barcode', { type: 'server', message: error.userMessage });
      } else if (isApiError(error) && error.isConflict) {
        notify.error(error, 'Someone else changed this item');
        void queryClient.invalidateQueries({ queryKey: inventoryKeys.items });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the item');
      }
    },
  });

  const rx = requiresPrescription(schedule, isControlled);
  const register = requiresRegister(schedule, isControlled);

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {item ? item.name : 'Add item'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <FormGrid columns={2}>
              <TextField
                control={form.control}
                name="code"
                label="Item code"
                required
                autoFocus={!item}
                disabled={readOnly}
                maxLength={32}
                hint="Short unique code, e.g. PARA500"
              />
              <TextField
                control={form.control}
                name="name"
                label="Name"
                required
                disabled={readOnly}
                maxLength={200}
                hint="As printed on invoices and labels"
              />
              <TextField
                control={form.control}
                name="genericName"
                label="Generic name"
                disabled={readOnly}
                hint="Used for generic substitution"
              />
              <TextField control={form.control} name="brandName" label="Brand" disabled={readOnly} />
              <SelectField
                control={form.control}
                name="type"
                label="Type"
                required
                disabled={readOnly}
                options={ITEM_TYPES.map((t) => ({ value: t, label: itemTypeLabel[t] }))}
              />
              <SelectField
                control={form.control}
                name="categoryId"
                label="Category"
                placeholder="—"
                disabled={readOnly}
                options={(categories.data ?? []).map((c) => ({ value: c.id, label: c.name }))}
              />
              <TextField
                control={form.control}
                name="strength"
                label="Strength"
                disabled={readOnly}
                hint="e.g. 500 mg"
              />
              <TextField
                control={form.control}
                name="dosageForm"
                label="Dosage form"
                disabled={readOnly}
                hint="e.g. Tablet, Injection"
              />
            </FormGrid>

            <Subtitle2 as="h3">Units</Subtitle2>
            <FormGrid columns={3}>
              <TextField
                control={form.control}
                name="baseUnit"
                label="Issue unit"
                required
                disabled={readOnly}
                maxLength={16}
                hint="What is sold and counted: TAB, ML, NOS"
              />
              <TextField
                control={form.control}
                name="purchaseUnit"
                label="Purchase unit"
                required
                disabled={readOnly}
                maxLength={16}
                hint="How vendors supply it: STRIP, BOX"
              />
              <NumberField
                control={form.control}
                name="purchaseUnitFactor"
                label={`${(baseUnit || 'Units').toUpperCase()} per ${(purchaseUnit || 'purchase unit').toUpperCase()}`}
                required
                disabled={readOnly}
                min={0}
              />
              <TextField
                control={form.control}
                name="packSize"
                label="Pack size"
                disabled={readOnly}
                hint="e.g. 10 x 10"
              />
            </FormGrid>

            <Subtitle2 as="h3">Tax and regulation</Subtitle2>
            <FormGrid columns={3}>
              <TextField
                control={form.control}
                name="hsnCode"
                label="HSN code"
                disabled={readOnly}
                maxLength={8}
                hint="4, 6 or 8 digits, e.g. 300490 for medicines"
              />
              <SelectField
                control={form.control}
                name="gstRate"
                label="GST rate"
                required
                disabled={readOnly}
                options={GST_RATES.map((r) => ({ value: String(r), label: formatPercent(r) }))}
              />
              <SelectField
                control={form.control}
                name="schedule"
                label="Drug schedule"
                required
                disabled={readOnly}
                hint={scheduleHint[schedule]}
                options={DRUG_SCHEDULES.map((s) => ({ value: s, label: scheduleLabel[s] }))}
              />
            </FormGrid>
            <FormGrid columns={2}>
              <SwitchField
                control={form.control}
                name="isControlled"
                label="Narcotic or psychotropic (NDPS)"
                disabled={readOnly}
                hint="Always recorded in the controlled-drug register"
              />
              <SwitchField control={form.control} name="isColdChain" label="Cold chain (2–8 °C)" disabled={readOnly} />
            </FormGrid>
            {type === 'Drug' && (
              <div className={styles.rules}>
                <Badge appearance="tint" color={rx ? 'warning' : 'success'}>
                  {rx ? 'Sold only against a prescription' : 'Can be sold over the counter'}
                </Badge>
                {register && (
                  <Badge appearance="tint" color="danger">
                    Written to the controlled-drug register
                  </Badge>
                )}
              </div>
            )}

            <Subtitle2 as="h3">Stock levels</Subtitle2>
            <FormGrid columns={3}>
              <SelectField
                control={form.control}
                name="vedClass"
                label="VED class"
                disabled={readOnly}
                options={VED_CLASSES.map((v) => ({ value: v, label: vedLabel[v] }))}
              />
              <NumberField
                control={form.control}
                name="reorderLevel"
                label="Reorder level"
                disabled={readOnly}
                min={0}
                hint="Suggest a purchase at or below this"
              />
              <NumberField
                control={form.control}
                name="reorderQuantity"
                label="Reorder quantity"
                disabled={readOnly}
                min={0}
              />
              <NumberField
                control={form.control}
                name="minimumStock"
                label="Minimum stock"
                disabled={readOnly}
                min={0}
              />
              <NumberField
                control={form.control}
                name="maximumStock"
                label="Maximum stock"
                disabled={readOnly}
                min={0}
                hint="0 = no maximum"
              />
            </FormGrid>

            <Subtitle2 as="h3">Other</Subtitle2>
            <FormGrid columns={2}>
              <TextField control={form.control} name="manufacturer" label="Manufacturer" disabled={readOnly} />
              <TextField
                control={form.control}
                name="barcode"
                label="Barcode"
                disabled={readOnly}
                maxLength={64}
                hint="Scan the pack to fill it"
              />
            </FormGrid>
            <SwitchField
              control={form.control}
              name="isActive"
              label="Active (can be ordered, received and sold)"
              disabled={readOnly}
            />
          </div>
        </DrawerBody>
        <DrawerFooter>
          {canManage && (
            <Button
              type="submit"
              appearance="primary"
              disabled={save.isPending}
              icon={save.isPending ? <Spinner size="tiny" /> : undefined}
            >
              {item ? 'Save' : 'Add item'}
            </Button>
          )}
          <Button onClick={guard.requestClose}>{canManage ? 'Cancel' : 'Close'}</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
