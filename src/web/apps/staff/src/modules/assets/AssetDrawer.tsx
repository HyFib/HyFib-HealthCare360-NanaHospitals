import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  Subtitle2,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  DateField,
  FormGrid,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { VendorPicker } from '@/modules/purchase/VendorPicker';
import { assetApi, assetKeys, type AssetDetail } from './api';
import {
  ASSET_CATEGORIES,
  assetCategoryLabel,
  CRITICALITIES,
  DEPRECIATION_METHODS,
  depreciationLabel,
  type AssetCategory,
  type AssetCriticality,
  type DepreciationMethod,
} from './labels';

const schema = z
  .object({
    name: z.string().trim().min(1, 'Enter the asset name.').max(200),
    category: z.enum(ASSET_CATEGORIES),
    make: z.string().trim().max(100),
    model: z.string().trim().max(100),
    serialNumber: z.string().trim().max(100),
    departmentId: z.string().nullable(),
    location: z.string().trim().max(200),
    custodianName: z.string().trim().max(120),
    purchaseDate: z.string().nullable(),
    purchaseCost: z.number({ error: 'Enter the cost (0 if donated).' }).min(0),
    vendorId: z.string().nullable(),
    vendorName: z.string(),
    purchaseOrderNumber: z.string().trim().max(32),
    invoiceNumber: z.string().trim().max(40),
    installationDate: z.string().nullable(),
    warrantyExpiry: z.string().nullable(),
    criticality: z.enum(CRITICALITIES),
    depreciationMethod: z.enum(DEPRECIATION_METHODS),
    usefulLifeYears: z.number({ error: 'Enter the useful life in years.' }).int('Whole years.').min(1).max(50),
    salvageValue: z.number({ error: 'Enter the salvage value (0 if none).' }).min(0),
    depreciationRatePercent: z.number().min(0).max(100).nullable(),
    requiresCalibration: z.boolean(),
    barcode: z.string().trim().max(64),
    notes: z.string().trim().max(2000),
  })
  .superRefine((v, ctx) => {
    if (v.purchaseDate && v.purchaseDate > todayIso()) {
      ctx.addIssue({ code: 'custom', path: ['purchaseDate'], message: 'The purchase date cannot be in the future.' });
    }
    if (v.installationDate && v.purchaseDate && v.installationDate < v.purchaseDate) {
      ctx.addIssue({ code: 'custom', path: ['installationDate'], message: 'Installation cannot be before purchase.' });
    }
    if (v.salvageValue > v.purchaseCost) {
      ctx.addIssue({
        code: 'custom',
        path: ['salvageValue'],
        message: 'Salvage value cannot exceed the purchase cost.',
      });
    }
    if (v.depreciationMethod === 'WrittenDownValue' && !(v.depreciationRatePercent && v.depreciationRatePercent > 0)) {
      ctx.addIssue({
        code: 'custom',
        path: ['depreciationRatePercent'],
        message: 'Enter the yearly WDV rate, e.g. 15%.',
      });
    }
  });

type AssetValues = z.infer<typeof schema>;

function toValues(a: AssetDetail | null): AssetValues {
  return {
    name: a?.name ?? '',
    category: (a?.category ?? 'MedicalEquipment') as AssetCategory,
    make: a?.make ?? '',
    model: a?.model ?? '',
    serialNumber: a?.serialNumber ?? '',
    departmentId: a?.departmentId ?? null,
    location: a?.location ?? '',
    custodianName: a?.custodianName ?? '',
    purchaseDate: a?.purchaseDate ?? null,
    purchaseCost: a?.purchaseCost ?? 0,
    vendorId: a?.vendorId ?? null,
    vendorName: a?.vendorName ?? '',
    purchaseOrderNumber: a?.purchaseOrderNumber ?? '',
    invoiceNumber: a?.invoiceNumber ?? '',
    installationDate: a?.installationDate ?? null,
    warrantyExpiry: a?.warrantyExpiry ?? null,
    criticality: (a?.criticality ?? 'Medium') as AssetCriticality,
    depreciationMethod: (a?.depreciationMethod ?? 'StraightLine') as DepreciationMethod,
    usefulLifeYears: a?.usefulLifeYears ?? 10,
    salvageValue: a?.salvageValue ?? 0,
    depreciationRatePercent: a?.depreciationRatePercent ?? null,
    requiresCalibration: a?.requiresCalibration ?? false,
    barcode: a?.barcode ?? '',
    notes: a?.notes ?? '',
  };
}

/** Asset register entry: identity, location, purchase and warranty, criticality and depreciation basis. */
export function AssetDrawer({
  asset,
  onClose,
  onCreated,
}: {
  asset: AssetDetail | null;
  onClose: () => void;
  onCreated?: (id: string) => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const form = useForm<AssetValues>({
    resolver: zodResolver(schema),
    defaultValues: toValues(asset),
    mode: 'onTouched',
  });
  const [method, vendorName, vendorId] = useWatch({
    control: form.control,
    name: ['depreciationMethod', 'vendorName', 'vendorId'],
  });

  const save = useMutation({
    mutationFn: (v: AssetValues) =>
      assetApi.save(asset?.id ?? null, {
        name: v.name,
        category: v.category,
        make: v.make || null,
        model: v.model || null,
        serialNumber: v.serialNumber || null,
        departmentId: v.departmentId,
        location: v.location || null,
        custodianUserId: asset?.custodianUserId ?? null,
        custodianName: v.custodianName || null,
        purchaseDate: v.purchaseDate,
        purchaseCost: v.purchaseCost,
        vendorId: v.vendorId,
        purchaseOrderNumber: v.purchaseOrderNumber || null,
        invoiceNumber: v.invoiceNumber || null,
        installationDate: v.installationDate,
        warrantyExpiry: v.warrantyExpiry,
        criticality: v.criticality,
        depreciationMethod: v.depreciationMethod,
        usefulLifeYears: v.usefulLifeYears,
        salvageValue: v.salvageValue,
        depreciationRatePercent: v.depreciationRatePercent,
        requiresCalibration: v.requiresCalibration,
        barcode: v.barcode || null,
        notes: v.notes || null,
        version: asset?.version ?? null,
      }),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      if (created) {
        notify.success(`Asset ${created.number} added`, 'Print the tag and stick it on the equipment.');
        onCreated?.(created.id);
      } else {
        notify.success('Asset saved');
      }
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'asset.duplicate_serial') {
        form.setError('serialNumber', { type: 'server', message: error.userMessage });
      } else if (isApiError(error) && error.isConflict) {
        notify.error(error, 'Someone else changed this asset');
        void queryClient.invalidateQueries({ queryKey: assetKeys.all });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the asset');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {asset ? `${asset.name} (${asset.assetTag})` : 'Add asset'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <FormGrid columns={2}>
              <TextField
                control={form.control}
                name="name"
                label="Name"
                required
                autoFocus={!asset}
                maxLength={200}
                hint="e.g. Ventilator, ICU bed 4"
              />
              <SelectField
                control={form.control}
                name="category"
                label="Category"
                options={ASSET_CATEGORIES.map((c) => ({ value: c, label: assetCategoryLabel[c] }))}
              />
              <TextField control={form.control} name="make" label="Make" />
              <TextField control={form.control} name="model" label="Model" />
              <TextField control={form.control} name="serialNumber" label="Serial number" />
              <TextField control={form.control} name="barcode" label="Barcode" hint="Scan the existing label, if any" />
            </FormGrid>
            <Subtitle2 as="h3">Where it is</Subtitle2>
            <FormGrid columns={3}>
              <SelectField
                control={form.control}
                name="departmentId"
                label="Department"
                placeholder="—"
                options={toOptions(departments.data)}
              />
              <TextField control={form.control} name="location" label="Location" hint="Building, floor, room" />
              <TextField
                control={form.control}
                name="custodianName"
                label="Custodian"
                hint="Who is responsible for it"
              />
            </FormGrid>
            <Subtitle2 as="h3">Purchase and warranty</Subtitle2>
            <FormGrid columns={3}>
              <DateField control={form.control} name="purchaseDate" label="Purchase date" max={todayIso()} />
              <NumberField control={form.control} name="purchaseCost" label="Cost (₹)" min={0} />
              <DateField control={form.control} name="installationDate" label="Installed on" />
              <DateField control={form.control} name="warrantyExpiry" label="Warranty until" />
              <TextField control={form.control} name="purchaseOrderNumber" label="PO number" />
              <TextField control={form.control} name="invoiceNumber" label="Invoice number" />
            </FormGrid>
            <VendorPicker
              label="Supplier"
              value={vendorId ? { name: vendorName } : null}
              onPick={(v) => {
                form.setValue('vendorId', v.id, { shouldDirty: true });
                form.setValue('vendorName', v.name, { shouldDirty: true });
              }}
              hint="Used for AMC/CMC contracts and service calls"
            />
            <Subtitle2 as="h3">Risk and upkeep</Subtitle2>
            <FormGrid columns={2}>
              <SelectField
                control={form.control}
                name="criticality"
                label="Criticality"
                hint="Critical equipment (ventilators, defibrillators) gets breakdowns handled first"
                options={CRITICALITIES.map((c) => ({ value: c, label: c }))}
              />
              <SwitchField
                control={form.control}
                name="requiresCalibration"
                label="Needs periodic calibration"
                hint="Measuring equipment: NABH/NABL requirement"
              />
            </FormGrid>
            <Subtitle2 as="h3">Depreciation</Subtitle2>
            <FormGrid columns={2}>
              <SelectField
                control={form.control}
                name="depreciationMethod"
                label="Method"
                options={DEPRECIATION_METHODS.map((m) => ({ value: m, label: depreciationLabel[m] }))}
              />
              <NumberField control={form.control} name="usefulLifeYears" label="Useful life (years)" min={1} max={50} />
              <NumberField control={form.control} name="salvageValue" label="Salvage value (₹)" min={0} />
              <NumberField
                control={form.control}
                name="depreciationRatePercent"
                label="Rate per year (%)"
                min={0}
                max={100}
                hint={
                  method === 'WrittenDownValue'
                    ? 'Required for written-down value'
                    : 'Blank = worked out from the useful life'
                }
              />
            </FormGrid>
            <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={2000} />
            {save.error &&
              isApiError(save.error) &&
              !save.error.isValidation &&
              save.error.code !== 'asset.duplicate_serial' && (
                <MessageBar intent="error">
                  <MessageBarBody>{save.error.userMessage}</MessageBarBody>
                </MessageBar>
              )}
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {asset ? 'Save' : 'Add asset'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
