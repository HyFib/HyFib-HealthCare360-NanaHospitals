import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  OverlayDrawer,
  Select,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { applyServerErrors, FormGrid, NumberField, SelectField, SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useFieldArray, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { insuranceQueries } from '@/modules/insurance/api';
import { searchServices } from './api';
import { TARIFF_TYPE_LABEL, TARIFF_TYPES, optionsOf, type TariffType } from './labels';
import { mastersApi, mastersKeys, type TariffPlanDetail } from './mastersApi';
import { hasAtMostTwoDecimals } from './money';
import { ActionError } from './ui';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  multiplier: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 120px auto', gap: '8px', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a code.')
    .max(32)
    .regex(/^[A-Za-z0-9][A-Za-z0-9._-]*$/, 'Use letters, digits, dots, dashes or underscores.'),
  name: z.string().trim().min(1, 'Enter a name.').max(128),
  type: z.enum(TARIFF_TYPES),
  payerId: z.string().nullable(),
  defaultDiscountPercent: z
    .number({ error: 'Enter a percentage.' })
    .min(0, 'Enter a percentage between 0 and 100.')
    .max(100, 'Enter a percentage between 0 and 100.')
    .refine(hasAtMostTwoDecimals, 'Use at most 2 decimal places.'),
  isDefault: z.boolean(),
  isActive: z.boolean(),
  description: z.string().max(500),
  bedMultipliers: z.array(
    z.object({
      bedServiceItemId: z.string().min(1, 'Choose a room / bed service.'),
      label: z.string(),
      multiplier: z.number({ error: 'Enter a multiplier.' }).min(0.1, 'Use 0.1 to 10.').max(10, 'Use 0.1 to 10.'),
    }),
  ),
});

type PlanForm = z.infer<typeof schema>;

/** Create or edit a tariff plan: who it is for, its default concession and room-class multipliers (MB-02). */
export function TariffPlanDrawer({ plan, onClose, onSaved }: { plan: TariffPlanDetail | null; onClose: () => void; onSaved: (plan: TariffPlanDetail) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const payers = useQuery(insuranceQueries.payers());
  const beds = useQuery({ queryKey: ['billing', 'bed-services'], queryFn: () => searchServices('', 'BedCharge'), staleTime: 10 * 60_000 });

  const form = useForm<PlanForm>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: plan?.code ?? '',
      name: plan?.name ?? '',
      type: plan?.type ?? 'General',
      payerId: plan?.payerId ?? null,
      defaultDiscountPercent: plan?.defaultDiscountPercent ?? 0,
      isDefault: plan?.isDefault ?? false,
      isActive: plan?.isActive ?? true,
      description: plan?.description ?? '',
      bedMultipliers: (plan?.bedMultipliers ?? []).map((m) => ({ bedServiceItemId: m.bedServiceItemId, label: m.bedServiceName ?? m.bedServiceCode ?? '', multiplier: m.multiplier })),
    },
  });
  const multipliers = useFieldArray({ control: form.control, name: 'bedMultipliers' });
  const type = useWatch({ control: form.control, name: 'type' });
  const chosenBeds = useWatch({ control: form.control, name: 'bedMultipliers' });

  const save = useMutation({
    mutationFn: (values: PlanForm) => {
      const request = {
        code: values.code.trim().toUpperCase(),
        name: values.name.trim(),
        type: values.type,
        payerId: values.type === 'General' || values.type === 'Staff' ? null : values.payerId,
        facilityId: null,
        defaultDiscountPercent: values.defaultDiscountPercent,
        isDefault: values.isDefault,
        isActive: values.isActive,
        description: values.description.trim() || null,
        bedMultipliers: values.bedMultipliers.map((m) => ({ bedServiceItemId: m.bedServiceItemId, multiplier: m.multiplier })),
        version: plan?.version ?? null,
      };
      return plan ? mastersApi.updatePlan(plan.id, request) : mastersApi.createPlan(request);
    },
    onSuccess: (saved) => {
      notify.success(plan ? 'Tariff plan saved' : 'Tariff plan created', saved.name);
      void queryClient.invalidateQueries({ queryKey: mastersKeys.tariffs });
      onSaved(saved);
    },
    onError: (error) => {
      applyServerErrors(error, form.setError);
    },
  });

  const payerCategories: Record<TariffType, string[]> = { General: [], Staff: [], Corporate: ['Corporate'], Insurance: ['Insurer', 'Tpa'], GovernmentScheme: ['GovernmentScheme'] };
  const payerOptions = (payers.data ?? []).filter((p) => payerCategories[type].includes(p.category)).map((p) => ({ value: p.id, label: p.name }));
  const unusedBeds = (beds.data ?? []).filter((b) => !chosenBeds.some((m) => m.bedServiceItemId === b.id));

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>
            {plan ? plan.name : 'New tariff plan'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <FormGrid columns={2} minColumnWidth={180}>
              <TextField control={form.control} name="code" label="Code" required disabled={Boolean(plan)} hint={plan ? 'Codes cannot be changed.' : 'e.g. CGHS, STAR-GOLD'} autoFocus={!plan} />
              <TextField control={form.control} name="name" label="Name" required autoFocus={Boolean(plan)} />
              <SelectField control={form.control} name="type" label="For" required options={optionsOf(TARIFF_TYPES, TARIFF_TYPE_LABEL)} />
              {payerOptions.length > 0 && <SelectField control={form.control} name="payerId" label="Payer" placeholder="Any payer of this kind" options={payerOptions} />}
            </FormGrid>
            <NumberField
              control={form.control}
              name="defaultDiscountPercent"
              label="Default concession (%)"
              hint="Applied to services priced on this plan where discounts are allowed"
              min={0}
              max={100}
              step={0.01}
            />
            <SwitchField control={form.control} name="isDefault" label="Default plan for this kind of payer" />
            <SwitchField control={form.control} name="isActive" label="Active" />
            <TextAreaField control={form.control} name="description" label="Description" rows={2} maxLength={500} />

            <Field label="Room-class multipliers" hint="Procedures, surgery, nursing and doctor visits are priced × this factor for patients in that room class">
              <div className={styles.form}>
                {multipliers.fields.map((field, index) => (
                  <div key={field.id} className={styles.multiplier}>
                    <Caption1>{field.label || 'Room / bed service'}</Caption1>
                    <NumberField control={form.control} name={`bedMultipliers.${index}.multiplier`} label="× price" min={0.1} max={10} step={0.05} />
                    <Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Remove ${field.label}`} onClick={() => multipliers.remove(index)} />
                  </div>
                ))}
                {unusedBeds.length > 0 && (
                  <Select
                    value=""
                    aria-label="Add a room class"
                    onChange={(_, d) => {
                      const bed = unusedBeds.find((b) => b.id === d.value);
                      if (bed) {
                        multipliers.append({ bedServiceItemId: bed.id, label: `${bed.name} (${bed.code})`, multiplier: 1 });
                      }
                    }}
                  >
                    <option value="">Add a room class…</option>
                    {unusedBeds.map((b) => (
                      <option key={b.id} value={b.id}>
                        {b.name} ({b.code})
                      </option>
                    ))}
                  </Select>
                )}
                {form.formState.errors.bedMultipliers && <Caption1 style={{ color: tokens.colorPaletteRedForeground1 }}>Check the multipliers: each must be between 0.1 and 10.</Caption1>}
              </div>
            </Field>
            <ActionError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button type="submit" appearance="primary" disabled={save.isPending} icon={save.isPending ? <Spinner size="tiny" /> : <AddRegular />}>
            {plan ? 'Save' : 'Create plan'}
          </Button>
          <Button onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}
